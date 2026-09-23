"""Exercise the broker against a separately started disposable Unity 6.3 LiveHost."""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import time
import urllib.request
import urllib.parse
import uuid

ROOT = None
BASE = None
TOKEN = None
ARGS = None
REPORT = {'checks': [], 'measurements': {}}

def rpc(method, params, request_id, session=None):
    headers = {'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json',
               'Accept': 'application/json', 'MCP-Protocol-Version': '2025-06-18'}
    if session:
        headers['Mcp-Session-Id'] = session
    body = json.dumps({'jsonrpc': '2.0', 'id': request_id, 'method': method, 'params': params}).encode()
    with urllib.request.urlopen(urllib.request.Request(BASE + '/mcp/', data=body, headers=headers), timeout=70) as response:
        raw = response.read()
        value = json.loads(raw)
        if 'error' in value:
            raise AssertionError(value['error'])
        return value['result'], response.headers.get('Mcp-Session-Id'), len(raw)

def tool(name, arguments=None, request_id=2, session=None, meta=None, allow_error=False):
    params = {'name': name, 'arguments': arguments or {}}
    if meta:
        params['_meta'] = meta
    result, _, size = rpc('tools/call', params, request_id, session)
    if result.get('isError') and not allow_error:
        raise AssertionError((name, result))
    return result.get('structuredContent'), size

def check(name, condition):
    assert condition, name
    REPORT['checks'].append(name)

def wait_refresh(session, label):
    refreshed, _ = tool('broker_start_refresh_compile', {}, request_id=8, session=session)
    REPORT[label + 'Start'] = refreshed
    job_id = refreshed.get('job_id') or refreshed.get('jobId') or refreshed.get('job', {}).get('job_id') or refreshed.get('job', {}).get('jobId')
    if not job_id:
        raise AssertionError(('refresh result has no durable job ID', refreshed))
    deadline = time.monotonic() + 120
    while True:
        job, _ = tool('broker_wait_job', {'jobId': job_id, 'timeoutSeconds': 30}, request_id=9, session=session)
        REPORT[label + 'Result'] = job
        state = job.get('status') or job.get('state') or job.get('job', {}).get('state')
        if state in ('completed', 'failed', 'cancelled') or time.monotonic() > deadline:
            break
    check(label + ' reaches evidenced terminal state', state == 'completed')
    return job

def main():
    with urllib.request.urlopen(BASE + '/health', timeout=5) as response:
        health = json.load(response)
    check('real Unity bridge connected', health['bridgeConnected'])
    ready = json.loads(ARGS.ready_file.read_text(encoding='utf-8-sig'))
    check('broker targets the exact disposable project',
          health.get('activeProjectKey') == ready['projectKey']
          and os.path.normcase(str(Path(health.get('activeProjectPath') or '').resolve()))
          == os.path.normcase(str(ARGS.project_path)))
    check('exact Unity 6.3 patch', ready['unityVersion'] == ARGS.expected_unity_version)
    REPORT['identity'] = ready
    first, session_a, _ = rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'ci-a', 'version': '1'}}, 1)
    _, session_b, _ = rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'ci-b', 'version': '1'}}, 1)
    check('independent HTTP sessions', bool(session_a and session_b and session_a != session_b))
    check('honest listChanged capability', first['capabilities']['tools']['listChanged'] is False)
    for sid in (session_a, session_b):
        tool('broker_activate_project', {'projectKey': ready['projectKey']}, session=sid)
    names, all_tools, page_sizes = [], [], []
    params = {'_meta': {'pageSize': 50}}
    while True:
        page, _, size = rpc('tools/list', params, 3, session_a)
        check('catalog page <=50', len(page['tools']) <= 50)
        names.extend(t['name'] for t in page['tools'])
        all_tools.extend(page['tools'])
        page_sizes.append(size)
        if not page.get('nextCursor'):
            break
        params = {'cursor': page['nextCursor']}
    check('complete unique catalog', len(names) == len(set(names)) == ready['registryCount'] + 8)
    REPORT['measurements']['catalog'] = {'tools': len(names), 'pages': len(page_sizes), 'largestPageBytes': max(page_sizes), 'totalWireBytes': sum(page_sizes)}
    (ROOT / 'live-catalog.json').write_text(json.dumps(all_tools), encoding='utf-8')
    core, _, core_size = rpc('tools/list', {'_meta': {'profile': 'core'}}, 3, session_a)
    REPORT['measurements']['catalog']['coreTools'] = len(core['tools'])
    REPORT['measurements']['catalog']['coreWireBytes'] = core_size
    check('core profile reduces discovery', len(core['tools']) < len(names) and not core.get('nextCursor'))
    modules = {t['_meta']['module'] for t in all_tools if not t['name'].startswith('broker_')}
    chosen_module = sorted(modules)[0]
    filtered, _, _ = rpc('tools/list', {'_meta': {'module': chosen_module, 'pageSize': 250}}, 3, session_a)
    check('module filter uses actual Unity metadata', any(not t['name'].startswith('broker_') for t in filtered['tools']) and all(t['name'].startswith('broker_') or t['_meta']['module'] == chosen_module for t in filtered['tools']))
    compact, compact_size = tool('get_server_health', {'compact': True}, session=session_a)
    full, full_size = tool('get_server_health', {}, session=session_b)
    check('health compact retains self-test', compact['selfTest']['valid'] and full['selfTest']['valid'])
    REPORT['measurements']['health'] = {'compactWireBytes': compact_size, 'fullWireBytes': full_size}
    # Same key and args from two independently initialized real HTTP clients.
    unique_name = 'AgentSafetySmoke_' + uuid.uuid4().hex[:10]
    key = uuid.uuid4().hex
    meta = {'idempotencyKey': key, 'projectKey': ready['projectKey'], 'expectedGenerationId': ready['generationId']}
    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
        futures = [pool.submit(tool, 'create_empty', {'name': unique_name}, 100 + i, session_a if i % 2 else session_b, meta) for i in range(20)]
        created = [f.result()[0] for f in futures]
    ids = {item['instanceId'] for item in created}
    check('20 concurrent same-key calls return one object', len(ids) == 1)
    instance = ids.pop()
    try:
        hierarchy, wire_size = tool('read_hierarchy', {'limit': 1, 'fields': []}, session=session_a)
        check('bounded hierarchy page', hierarchy['returned'] <= 1)
        REPORT['measurements']['hierarchyPageWireBytes'] = wire_size
        # Enumerate with stable cursors and verify there is exactly one effect.
        all_nodes, request_args = [], {'limit': 100, 'fields': []}
        while True:
            h, _ = tool('read_hierarchy', request_args, request_id=4, session=session_a)
            all_nodes.extend(h['nodes'])
            if not h.get('nextCursor'):
                break
            request_args['cursor'] = h['nextCursor']
        check('one actual creation in Unity hierarchy', len([n for n in all_nodes if n['name'] == unique_name]) == 1)
        refused, _ = tool('create_empty', {'name': 'MustNeverExist'}, request_id=6, session=session_b,
                          meta={'expectedGenerationId': 'not-the-running-generation'}, allow_error=True)
        check('stale generation write rejected', refused.get('code') == 'STALE_GENERATION')
    finally:
        tool('delete_object', {'instanceId': instance}, request_id=7, session=session_a)
    idle_job = wait_refresh(session_a, 'noOpRefresh')
    check('no-op refresh reports no compilation', idle_job.get('result', {}).get('compiled') is False)
    idle_status, _ = tool('broker_get_status', {}, session=session_a)
    check('no-op refresh retains generation', idle_status['generationId'] == ready['generationId'])
    direct_idle, _ = tool('wait_for_compile_clean', {'timeoutSeconds': 5}, session=session_a)
    check('direct compile wait observes stable idle', direct_idle['compiled'] is False and direct_idle['elapsedSeconds'] >= 0.5)
    REPORT['directNoOpRefresh'] = direct_idle
    reload_source = ARGS.project_path / 'Assets/Ci/AgentSmokeReload.cs'
    reload_source.write_text('// Disposable integration test\ninternal static class AgentSmokeReload { public const string Version = "' + uuid.uuid4().hex + '"; }\n', encoding='utf-8')
    compiled_job = wait_refresh(session_a, 'refresh')
    check('queued compilation does not complete as no-op', compiled_job.get('result', {}).get('event') == 'compilation_finished')
    check('successful compile waits for reconnect', compiled_job.get('result', {}).get('reconnected') is True)
    # Broker hello is authoritative; the helper's ready-file may lag its reply.
    ready_after, _ = tool('broker_get_status', {}, request_id=10, session=session_a)
    check('real compile changed bridge generation', ready_after['generationId'] != ready['generationId'])
    after, _ = tool('self_test', {}, request_id=10, session=session_a)
    check('agent reads work after reload', after['valid'])
    REPORT['passed'] = True
    REPORT['completedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--broker-url', required=True, help='Private loopback broker HTTP origin')
    parser.add_argument('--ready-file', required=True, type=Path)
    parser.add_argument('--project-path', required=True, type=Path)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--expected-unity-version', default='6000.3.5f2')
    ARGS = parser.parse_args()
    origin = urllib.parse.urlparse(ARGS.broker_url)
    if origin.scheme != 'http' or origin.hostname not in ('127.0.0.1', 'localhost', '::1') or origin.username or origin.password:
        parser.error('--broker-url must be a private loopback http:// origin')
    ARGS.project_path = ARGS.project_path.resolve()
    if '/.validation/unity63-ci/' not in ARGS.project_path.as_posix().lower() or not (ARGS.project_path / 'Assets/Ci/Editor/AgentEnvironmentCi.cs').is_file():
        parser.error('--project-path must be a disposable Unity63 CI fixture with LiveHost')
    TOKEN = os.environ.get('UNITY_MCP_CI_BROKER_TOKEN')
    if not TOKEN:
        parser.error('UNITY_MCP_CI_BROKER_TOKEN is required')
    ROOT = ARGS.output_dir.resolve()
    ROOT.mkdir(parents=True, exist_ok=True)
    BASE = ARGS.broker_url.rstrip('/')
    try:
        main()
    except Exception as error:
        REPORT['passed'] = False
        REPORT['error'] = repr(error)
        raise
    finally:
        (ROOT / 'live-agent-smoke-report.json').write_text(json.dumps(REPORT, indent=2), encoding='utf-8')
        print(json.dumps({'passed': REPORT.get('passed'), 'checks': len(REPORT['checks']), 'measurements': REPORT['measurements'], 'error': REPORT.get('error')}, indent=2))
