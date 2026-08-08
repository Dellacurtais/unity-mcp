# Scene Modeling UX 2.0 — implementação completa

Atualizado em 2026-08-03. Este documento registra a fase **Scene Modeling UX 2.0** do Asset Modeler: a camada que transforma o plugin de "painel técnico que chama algoritmos" em uma experiência de modelagem nativa do Scene View da Unity. A especificação normativa permanece em `asset-modeler-complete-spec.md`; o estado das fases 0–8 em `asset-modeler-implementation-status.md`.

## Consolidação prévia

Antes desta fase, as alterações de Object Tools foram fechadas e validadas:

- commit `205048b` — remoção da barra nativa duplicada (Mover/Rotação/Escala/Bounds) e evolução das abas Transformar (presets, vértices selecionados, centralizar pivot), Espelhar (plano offset + solda de emenda), Array (IncludeSource + Alinhar/Distribuir), Combinar (nome, layer/tag/static, remover originais) e Exportar (nome único, lote, revelar, fallback `.asset`).
- Validação: compilação csc/Roslyn contra Unity 6000.3.5f2 — 0 erros nos arquivos alterados.

## Arquitetura da fase

A UX 2.0 introduz o namespace `Maicon.SceneAnalyzer.Editor.Modeling.SceneUx` com cinco arquivos novos e integra-se à janela e ao scene controller existentes **sem duplicar lógica**: a seleção de faces com marquee continua pertencendo ao `AssetModelerSceneController` (código provado), e o tool nativo assume os demais modos.

```
Editor/Modeling/SceneUx/
├── AssetModelerModelingState.cs    estado de sessão, modos, simetria, snap, atalhos
├── AssetModelerToolContext.cs      EditorToolContext nativo ("Asset Modeler")
├── AssetModelerModelingTool.cs     EditorTool: picking por modo + manipuladores
├── AssetModelerMeshDiagnostics.cs  diagnósticos de mesh (compute + desenho)
└── AssetModelerOverlays.cs         Overlays nativos (toolbar + diagnostics)
```

Integrações:

- `Editor/Modeling/UI/AssetModelerSceneController.cs` — gate quando o contexto nativo está ativo; membros internalizados (`AttachedWindow`, `RefreshGeometry`, `CurrentTopology`, `CurrentVertices`, `DrawFaceSelection`).
- `Editor/Modeling/UI/AssetModelerWindow.cs` — acessores internos para o tool (`SceneTopologyTool`, `SceneDeformMode`, `SceneExtrudeDistance`, `SceneSplinePoints`, etc.), seleção genérica por tipo de elemento, IDs movidos para foldout avançado e auto-derivação de loops/arestas a partir da seleção.

## 1. EditorToolContext e Overlays nativos

### `AssetModelerToolContext` (`AssetModelerToolContext.cs`)

- `[EditorToolContext("Asset Modeler", typeof(GameObject))]` registra o contexto na toolbar de ferramentas da Unity.
- `GetEditorToolType` resolve sempre para `AssetModelerModelingTool`.
- `IsActive` (`ToolManager.activeContextType`) é o gate usado pelo scene controller e pelos overlays.
- Ativação programática via `AssetModelerModelingState.ActivateMode(mode)` (atalhos e overlay chamam `ToolManager.SetActiveContext`).

### Overlays (`AssetModelerOverlays.cs`)

Dois `IMGUIOverlay` nativos (`UnityEditor.Overlays`), ancoráveis/movíveis/fecháveis pelo usuário como qualquer overlay do Scene View:

- **`asset-modeler-modeling` ("Asset Modeler")** — três linhas:
  1. seletor de modos (Objeto, Vértice, Aresta, Face, Região, Bone);
  2. orientação (Global/Local/Normal) + toggles Simetria X e Snap;
  3. ações contextuais: "Criar cópia editável" (sem documento) ou Preview/Aplicar/× (com documento e ferramenta com preview).
  Se o contexto está inativo, o overlay oferece o botão "Ativar Asset Modeler".
- **`asset-modeler-diagnostics` ("Mesh Diagnostics")** — toggles com contadores ao vivo: bordas abertas, non-manifold, normais invertidas e UV seams.

A toolbar IMGUI fixa do `AssetModelerSceneController` deixa de ser desenhada enquanto o contexto nativo está ativo — os overlays a substituem. Sem o contexto, o comportamento legado permanece intacto.

## 2. Modos de seleção

`AssetModelerEditMode` (persistido em `SessionState`, sobrevive a Domain Reload):

| Modo | Interação | Armazenamento |
|---|---|---|
| **Objeto** | Gizmos nativos da Unity, sem captura | — |
| **Vértice** | Clique seleciona o vértice mais próximo (raio 14 px); Shift adiciona; Ctrl/Cmd remove; clique no vazio limpa | `MeshElementKind.Vertex` |
| **Aresta** | Clique na aresta mais próxima (distância ao segmento, 12 px); **duplo clique = loop**; **Alt+duplo clique = ring** | `MeshElementKind.Edge` (pares planos `[a,b,…]`) |
| **Face** | Implementação existente do controller: clique, duplo clique (ilha), marquee contido/cruzado | `MeshElementKind.Face` |
| **Região** | Clique numa face seleciona a região salva que a contém (`MeshNamedRegion`) | converte para o kind da região |
| **Bone** | Clique no bone mais próximo do `SkinnedMeshRenderer` (raio 18 px) seleciona os vértices cujo peso dominante é daquele bone | `MeshElementKind.Vertex` |

### Convenção de arestas

A seleção de arestas usa pares planos de vértices `[a0,b0, a1,b1, …]` — o mesmo formato que `ExtrudeBoundaryEdgesOperation` já aceitava como texto. Isso elimina a digitação de pares no fluxo comum: a operação deriva os pares da seleção (ver §5).

### Loop e Ring (`AssetModelerModelingTool`)

- **Loop**: primeiro tenta os loops de borda exatos (`HalfEdgeTopology.BoundaryLoops()`); se a aresta não é de borda, aplica caminhada gulosa escolhendo, a cada vértice, a aresta mais paralela à atual (|dot| > 0.35) com exatamente duas faces — heurística estável para malhas trianguladas. Limite de 512 arestas.
- **Ring**: a partir das duas faces adjacentes, escolhe a aresta mais paralela que compartilha um vértice com a atual (|dot| > 0.5), propagando em faixa. Limite de 512 arestas.

Ambos são heurísticas de UX documentadas — malhas puramente trianguladas não têm loops/rings canônicos como malhas de quads.

### Simetria

`AssetModelerModelingState.ExpandSymmetric` expande seleções de vértices/arestas pelo plano local X=0, agrupando vértices por bucket quantizado `(|x|, y, z)` com tolerância 1e-4. Atalho **Alt+X**. Aplicada no clique de vértice e aresta.

## 3. Manipuladores no Scene View

Executam dentro do fluxo transacional existente: o arraste atualiza o parâmetro e regenera o **preview** (com throttle); a aplicação final continua exigindo o botão Aplicar.

| Ferramenta | Manipulador |
|---|---|
| **Extrudar faces** (modo Face) | Seta na normal média da seleção (`Handles.Slider`); o arraste define a distância, com snap opcional |
| **Bevel** (modo Face) | Mesma seta; o delta é normalizado pela escala da seleção e mapeado ao inset (0–0.95) |
| **Soft selection / Deform** | `Handles.RadiusHandle` no centro dos bounds controla o raio de influência, com disco visível |
| **Grab** (Deform) | `PositionHandle`: o deslocamento vira direção + força da deformação |
| **Spline** (Deform) | Um `PositionHandle` por ponto da curva, com polilinha conectando os pontos |
| **Bend/Twist/Taper/Flatten** | Seta de eixo desenhada no centro dos bounds (visual; ângulo/força seguem na janela) |

Throttle de preview: regenera somente quando o valor varia mais que `SnapStep/2` (com snap) ou 0.01 (sem snap), evitando rebuilds por frame em meshes densas.

### Snap, orientação e atalhos

- **Snap** (toggle no overlay ou **Alt+S**): arredonda distâncias dos manipuladores ao passo configurado (`SnapStep`, padrão 0.05).
- **Orientação** Global/Local/Normal: estado persistido no overlay, reservado aos manipuladores de deformação (o extrude é inerentemente por-normal).
- **Atalhos** (`ShortcutManager`, reconfiguráveis em Edit > Shortcuts): **1–6** trocam de modo (ativando o contexto se necessário), **Alt+X** simetria, **Alt+S** snap.

## 4. Diagnostics Overlay

`AssetModelerMeshDiagnostics` computa sob demanda (cache por instância de topologia + mesh) e desenha com orçamento de 4000 linhas por categoria:

| Diagnóstico | Fonte | Cor |
|---|---|---|
| Bordas abertas (buracos) | `HalfEdgeTopology.BoundaryEdges` | azul |
| Non-manifold | `HalfEdgeTopology.NonManifoldEdges` (>2 faces por aresta) | vermelho |
| Normais invertidas | dot(normal geométrica, normal armazenada) < 0 por face | laranja |
| UV seams | aresta interior cujos UVs de canto divergem entre as duas faces (ε = 1e-4) | magenta |

Os contadores aparecem nos toggles do overlay; os destaques são desenhados pelo tool enquanto o contexto está ativo.

## 5. Janela: IDs só no modo avançado

- Novo foldout **"Avançado: seleção por IDs"** (`_showAdvancedIds`) em Extrudar arestas, Bridge e Fechar buraco. Os campos de texto de IDs deixam o fluxo comum.
- **Auto-derivação** em `CreateCurrentMeshOperation`:
  - `ExtrudeEdges` → `ResolveEdgePairs()`: usa o campo avançado se preenchido; senão converte a seleção de arestas do Scene View (pares planos).
  - `FillHole` → `ResolveHoleLoop()`: usa o campo avançado se preenchido; senão detecta automaticamente o **maior loop de borda** da mesh via `BoundaryLoops()`.
  - `BridgeLoops` permanece manual (dois loops ordenados são semanticamente ambíguos para auto-derivação).

## 6. Contrato entre tool, controller e janela

```
AssetModelerToolContext.IsActive
 ├─ true  → controller: sem toolbar IMGUI; picking só no modo Face
 │          tool: picking Vertex/Edge/Region/Bone + manipuladores + diagnósticos
 └─ false → comportamento legado inalterado (toolbar IMGUI + picking de faces)

Janela (internal): SceneTopologyTool, SceneDeformMode, SceneDeformAxis,
  SceneExtrudeDistance, SceneBevelInset, SceneDeformRadius, SceneDeformStrength,
  SceneDeformDirection, SceneSplinePoints, SceneStatus, SceneSelectRegion,
  SceneSelectElements(kind, indices, additive, remove, source)
Controller (internal): AttachedWindow, CurrentTopology, CurrentVertices,
  RefreshGeometry, DrawFaceSelection, TryPickFace, CollectConnectedFaces
```

## 7. Validação

- **Oráculo de compilação**: csc (Roslyn 8.0.422) contra as DLLs do Unity 6000.3.5f2 — 256 fontes, incluindo toda a camada SceneUx. Resultado: **0 erros nos arquivos novos/alterados**. Os únicos erros são os pré-existentes `PhysicsAuthor.cs:886,896` (`PhysicMaterial` → `PhysicsMaterial`), artefato de compilar fora do Unity (o atributo `UnityUpgradable` vira erro no csc puro e é auto-resolvido pelo API Updater do Editor).
- A busca de documentação indexada (rag_search_docs) estava indisponível nesta instância stdio; as APIs `EditorToolContext`, `EditorTool`, `IMGUIOverlay`/`OverlayAttribute` e `ShortcutAttribute` foram validadas diretamente pelo oráculo de compilação contra `UnityEditor.dll`.
- **Pendente no Editor**: suíte EditMode do pacote (oracle: 224 testes) e validação interativa do Scene View (picking, manipuladores, overlays) — não executáveis neste shell.

## 8. Limites conhecidos desta fase

- Marquee (seleção por área) existe apenas no modo Face; Vertex/Edge usam clique.
- Loop/Ring em malhas 100% trianguladas são heurísticas por paralelismo, não topológicas.
- Seleções de aresta não sobrevivem a rebuilds de topologia (`RemapTopologyPreservingVertices` preserva apenas vértices); `StableElementId` ainda não alimenta a seleção — item do Geometry Core 2.0.
- Orientação Global/Local ainda não altera o extrude (inerente por-normal); reservada aos deformadores.
- Surface Fit manipulável (anchors, máscara pintável) e preview ao vivo multipose ficam para o Surface Fit 2.0.

## 9. Roadmap pós-UX 2.0 (sequência recomendada)

1. **Geometry Core 2.0** — `StableElementId` real na seleção, `SelectionRemap` através de rebuilds, dissolve, retriangulação, decimation, correção de winding.
2. **Deform 2.0** — ARAP local-global com SVD (o atual é solver iterativo de preservação de distâncias), multirresolução, Jobs/Burst, preview progressivo.
3. **Surface Fit 2.0** — SDF volumétrico, auto-interseção roupa-roupa, fitting rig-aware, envelope multipose otimizado simultaneamente.
4. **Smart Split AI** — múltiplos renderers, landmarks, sementes mínimas, correção automática de regiões ambíguas, linguagem natural tipada.
5. **Produção 1.0** — testes runtime de personagens modulares, animações reais, URP/HDRP, matriz Unity 6000.1–6000.3, recuperação após Domain Reload, benchmark contínuo.
