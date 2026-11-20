# Asset Modeler — implementação integrada das fases 0–8

Atualizado em 2026-08-02. Este documento registra o corte vertical executável; a especificação normativa permanece em `asset-modeler-complete-spec.md`.

## Backup e segurança

- checkpoint anterior: commit `5a415c28218252d21b3a85cb08370afaf72b1cea`;
- branch de recuperação: `backup/asset-modeler-before-all-phases-20260802`;
- meshes importadas são somente leitura; edição começa por cópia `.asset` independente;
- operações usam revisão, preview hash, Undo e validação antes do commit;
- reimport da fonte produz `SOURCE_DRIFTED` e exige rebase ou descarte.

## Estado por fase

| Fase | Corte integrado |
|---|---|
| 0 | asmdefs, fixtures Humanoid/Generic/static, gate estático e benchmark de 100 mil triângulos |
| 1 | `MeshEditDocument`, cópia editável, todos os canais, modifier stack, rebuild, commit/rebase/discard |
| 2 | janela Asset Modeler, gizmos nativos, preview em Scene View, snap XYZ/vertex/edge/face/bone/socket, arrays, align, mirror, pivot, reparent e merge com compactação real de materiais |
| 3 | Smart Body Split Core/HandsAndFeet/Modular, pesos, preview, extração exata, fechamento inteligente dos cortes, `BodyPartSet`, manifesto e Prefab modular |
| 4 | Generic/static fallback, presets FPS/dano/roupa, edição de fronteira, seam anchors, compatibilidade, recombinação e validação em poses/clips |
| 5 | half-edge orientada, boundaries/non-manifold/components, cleanup, weld, subdivide, refino adaptativo, extrude faces/arestas, inset/bevel, bridge e fill hole com transferência de canais |
| 6 | soft selection, grab/smooth/relax/flatten/inflate, bend/twist/taper, lattice, spline, Laplacian e ARAP canceláveis |
| 7 | BVH multi-target, clearance, projeção anticolisão, refino automático, shape preservation Laplacian e avaliação atual/multipose por `AnimationClip` |
| 8 | 28 ferramentas MCP no mesmo command service, staging validado de assets externos, exportação FBX opcional e contratos de confirmação |

## Fluxo recomendado

1. Abra `Window > Asset Modeler (Unity MCP)`.
2. Selecione um objeto e use **Criar cópia editável**.
3. Gere **Preview**, revise o wireframe e só então use **Aplicar**.
4. Para personagens, abra a aba **Split**, escolha o preset e revise cores/fronteiras.
5. Para roupa, use **Vestir**, configure clearance e mantenha o refino automático ativo.
6. Valide a mesh/poses, faça commit e exporte FBX apenas se `com.unity.formats.fbx` estiver instalado.

## MCP disponível

O catálogo inclui sessões, seleção/regiões, preview/apply, modifier stack, Smart Body Split, Surface Fit, staging e FBX. Escritas topológicas e fitting exigem `previewHash`; writes de alto risco exigem confirmação.

## Validação deste corte

- gate estático Phase 0: aprovado;
- testes focados Asset Modeler: 31 aprovados;
- suíte EditMode do pacote em Unity 6000.3.5f2: 224 aprovados, 0 falhas, 3 ignorados;
- benchmark explícito: 100.000 triângulos, captura 6 ms, análise 132 ms nesta máquina.

## Limites deliberados antes de chamar de release 1.0

- o ARAP atual é um solver iterativo de preservação de distâncias, não uma implementação local-global com SVD;
- o multipose Surface Fit mede todos os samples e impede aprovação silenciosa, mas ainda não otimiza simultaneamente o envelope inteiro;
- auto-interseção roupa-roupa e SDF volumétrico ainda não substituem a projeção BVH contra o corpo;
- ProBuilder e FBX Exporter continuam adaptadores opcionais;
- casos estáticos semanticamente ambíguos usam heurística e devem ser revisados no preview; um sidecar remoto futuro deve permanecer opt-in.

Esses itens são endurecimento de qualidade, não caminhos paralelos de mutação: UI e MCP continuam usando os mesmos documentos, operações, previews e validadores.
