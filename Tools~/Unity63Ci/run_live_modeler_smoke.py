"""Verify the Asset Modeler tool contracts through a private broker and disposable Unity fixture."""
import argparse
import json
import os
from pathlib import Path
import urllib.parse
import urllib.request
import uuid
import run_live_agent_smoke as transport


def main(args):
    ready = json.loads(args.ready_file.read_text(encoding='utf-8-sig'))
    with urllib.request.urlopen(transport.BASE + '/health', timeout=5) as response:
        health = json.load(response)
    transport.check('exact disposable Unity project', health.get('bridgeConnected')
                    and health['activeProjectKey'] == ready['projectKey']
                    and Path(health['activeProjectPath']).resolve() == args.project_path)
    transport.check('Unity 6.3 patch', ready['unityVersion'] == '6000.3.5f2')
    _, session, _ = transport.rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
        'clientInfo': {'name': 'modeler-ci', 'version': '1'}}, 1)
    transport.tool('broker_activate_project', {'projectKey': ready['projectKey']}, session=session)
    sequence = 10

    def call(name, arguments, error=False):
        nonlocal sequence
        sequence += 1
        payload = {'name': name, 'arguments': arguments}
        result, _, _ = transport.rpc('tools/call', payload, sequence, session)
        value = result.get('structuredContent') or {}
        if result.get('isError') and value.get('code') == 'CONFIRMATION_REQUIRED':
            # Only the explicitly authored disposable-fixture mutations below
            # are approved here. Never accept an arbitrary operation or project.
            token = value.get('data', {}).get('confirmationToken')
            if not token:
                raise AssertionError(result)
            sequence += 1
            payload['arguments'] = {**arguments, '_confirmationToken': token}
            result, _, _ = transport.rpc('tools/call', payload, sequence, session)
            value = result.get('structuredContent') or {}
        if error:
            transport.check(name + ' rejects stale request', bool(result.get('isError')))
        elif result.get('isError'):
            raise AssertionError((name, result))
        return value

    tools = {}
    query = {'_meta': {'pageSize': 50}}
    while True:
        page, _, _ = transport.rpc('tools/list', query, 3, session)
        tools.update({item['name']: item for item in page['tools']})
        if not page.get('nextCursor'):
            break
        query = {'cursor': page['nextCursor']}
    names = ['mesh_analyze_cleanup', 'mesh_preview_cleanup', 'mesh_apply_cleanup', 'model_preview_scale', 'model_apply_scale']
    transport.check('five preparation tools discoverable', all(name in tools for name in names))
    transport.check('cleanup has typed opt-in flags', tools['mesh_preview_cleanup']['inputSchema']['properties']['removeLooseLines']['type'] == 'boolean')
    transport.check('scale advertises explicit modes', tools['model_preview_scale']['inputSchema']['properties']['mode']['enum'] == ['apply_current_scale', 'target_dimension'])
    modeling_tools = set()
    query = {'_meta': {'profile': 'modeling', 'pageSize': 50}}
    while True:
        page, _, _ = transport.rpc('tools/list', query, 4, session)
        modeling_tools.update(item['name'] for item in page['tools'])
        if not page.get('nextCursor'):
            break
        query = {'cursor': page['nextCursor']}
    transport.check('modeling profile includes cleanup and both scale tools', set(names) <= modeling_tools)
    roots = []
    operation_failed = False
    try:
        original = call('create_primitive', {'kind': 'Cube', 'name': 'ModelerSmoke_' + uuid.uuid4().hex[:8]})
        roots.append({'instanceId': original['instanceId']})
        session_result = call('mesh_begin_edit_session', {**roots[0], 'outputFolder': 'Assets/ModelerSmoke/Editable'})
        roots.append({'target': session_result['editableRoot']})
        doc = {'documentAssetPath': session_result['documentAssetPath']}
        diagnosis = call('mesh_analyze_cleanup', {**doc, 'limit': 1})
        transport.check('bounded cleanup diagnosis', len(diagnosis['components']) == 1 and diagnosis['hasMore'])
        operation = {**doc, 'expectedTopologyRevision': session_result['topologyRevision'],
                     'expectedGeometryRevision': session_result['geometryRevision']}
        stale_components = call('mesh_preview_cleanup', {**operation, 'removeComponentIds': [0],
                                'expectedMeshHash': '0' * 64}, error=True)
        transport.check('component selection is bound to diagnosis hash', stale_components['code'] == 'STALE_MESH_HASH')
        preview = call('mesh_preview_cleanup', operation)
        transport.check('preview preserves valid cube', preview['valid'] and preview['removedVertices'] == 0 and preview['removedFaces'] == 0)
        applied = call('mesh_apply_cleanup', {**operation, 'previewHash': preview['previewHash']})
        transport.check('cleanup write advances revision', applied['topologyRevision'] > operation['expectedTopologyRevision'])
        rejected = call('mesh_apply_cleanup', {**operation, 'previewHash': preview['previewHash']}, error=True)
        transport.check('cleanup stale revision error is specific', rejected['code'] == 'STALE_MESH_REVISION')
        size = {**roots[1], 'mode': 'target_dimension', 'axis': 'y', 'targetDimension': 1.8,
                'outputFolder': 'Assets/ModelerSmoke/Normalized'}
        scale_preview = call('model_preview_scale', size)
        transport.check('size preview reaches requested height', abs(scale_preview['boundsAfter']['size'][1] - 1.8) < 1e-5)
        rejected = call('model_apply_scale', {**size, 'expectedSourceHash': '0' * 64, 'previewHash': scale_preview['previewHash']}, error=True)
        transport.check('scale stale source error is specific', rejected['code'] == 'STALE_MODEL_SOURCE')
        normalized = call('model_apply_scale', {**size, 'expectedSourceHash': scale_preview['sourceHash'], 'previewHash': scale_preview['previewHash']})
        roots.append({'target': normalized['editableRoot']})
        transport.check('normalized copy has prefab output', normalized['resultRootScale'] == [1, 1, 1]
                        and any(path.endswith('.prefab') for path in normalized['createdAssetPaths']))
        observed = call('model_preview_scale', {**roots[2], 'mode': 'apply_current_scale'})
        transport.check('fresh observation confirms normalized mesh dimensions', observed['sourceRootScale'] == [1, 1, 1]
                        and abs(observed['boundsBefore']['size'][1] - 1.8) < 1e-5)
        transport.REPORT['identity'] = ready
        transport.REPORT['createdAssetPaths'] = normalized['createdAssetPaths']
        transport.REPORT['passed'] = True
    except BaseException:
        operation_failed = True
        raise
    finally:
        cleanup_failures = []
        for target in reversed(roots):
            try:
                deleted = call('delete_object', {'instanceId': target.get('instanceId') or target['target']['instanceId']})
                if deleted.get('deleted') is not True:
                    raise AssertionError(('delete_object did not confirm deletion', deleted))
            except Exception as exc:
                cleanup_failures.append({'target': target, 'error': repr(exc)})
        transport.REPORT['cleanupFailures'] = cleanup_failures
        if cleanup_failures:
            transport.REPORT['passed'] = False
            if not operation_failed:
                raise AssertionError(f'{len(cleanup_failures)} disposable objects could not be removed; see cleanupFailures')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--broker-url', required=True)
    parser.add_argument('--ready-file', type=Path, required=True)
    parser.add_argument('--project-path', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    origin = urllib.parse.urlparse(args.broker_url)
    if origin.scheme != 'http' or origin.hostname not in ('127.0.0.1', 'localhost', '::1') or origin.username or origin.password:
        parser.error('A private loopback broker origin is required.')
    args.project_path = args.project_path.resolve()
    if '/.validation/unity63-ci/' not in args.project_path.as_posix().lower() or not (args.project_path / 'Assets/Ci/Editor/AgentEnvironmentCi.cs').is_file():
        parser.error('Only a disposable Unity63Ci fixture is allowed.')
    transport.BASE = args.broker_url.rstrip('/')
    transport.TOKEN = os.environ.get('UNITY_MCP_CI_BROKER_TOKEN')
    if not transport.TOKEN:
        parser.error('UNITY_MCP_CI_BROKER_TOKEN is required.')
    args.output_dir.mkdir(parents=True, exist_ok=True)
    try:
        main(args)
    except Exception as exc:
        transport.REPORT.update(passed=False, error=repr(exc))
        raise
    finally:
        (args.output_dir / 'modeler-smoke.json').write_text(json.dumps(transport.REPORT, indent=2), encoding='utf-8')
        print(json.dumps(transport.REPORT, indent=2))
