# Asset Modeler e Smart Body Split — especificação completa

> Status: especificação para implementação
>
> Baseline verificada: `com.maicon.scene-analyzer` `0.18.0`, Unity `6000.1`
>
> Escopo: Editor Unity, ferramentas MCP, Coder Chat, sidecar opcional e componentes mínimos de runtime para personagens modulares

## 1. Resumo executivo

O pacote deve evoluir de uma infraestrutura de inspeção e automação para um editor 3D nativo do Unity, denominado **Asset Modeler**. O módulo reutiliza a Scene View, Hierarchy, Inspector, MCP autenticado, Coder Chat, Undo, Prefabs, renderização e picking já existentes. A principal entrega nova é um núcleo geométrico editável, compartilhado pela interface manual e por automações de IA.

O **Smart Body Split** será uma ferramenta de primeira classe desse editor. O usuário seleciona um personagem com ou sem rig, descreve as partes desejadas — por exemplo, “cabeça, braços, peitoral e pernas” — e recebe uma proposta visual. Por padrão, uma única confirmação cria meshes separadas, preservando rigorosamente materiais, UVs, normais, tangentes, bone weights, bind poses e blend shapes.

A IA não escreve arrays de vértices nem corta a mesh diretamente. Ela converte a intenção do usuário em uma especificação tipada e auxilia a classificação semântica. A extração das partes é executada por um algoritmo local, determinístico, validável, com preview, Undo e relatório de fidelidade.

## 2. Decisões de produto

1. A Scene View é o viewport oficial do Asset Modeler; não será criada uma segunda câmera, seleção ou navegação paralela.
2. O FBX importado permanece imutável. Toda edição começa por uma cópia `.asset` e, quando aplicável, um Prefab Variant.
3. Interface, menus, Coder Chat e MCP chamam o mesmo `ModelingCommandService`.
4. ProBuilder é um adaptador opcional para operações compatíveis, não o formato interno universal.
5. Alterações são não destrutivas até o commit explícito.
6. Operações topológicas usam revisão e IDs estáveis; solicitações obsoletas são rejeitadas.
7. Nenhum canal de mesh pode desaparecer silenciosamente.
8. O Smart Body Split classifica faces inteiras. Vértices de fronteira são duplicados entre as partes, evitando buracos e mantendo a animação coincidente nas costuras.
9. Para rigs, preservar o esqueleto completo é o padrão. Remover ossos não utilizados é uma otimização opcional e validada.
10. A IA reduz interação, mas o usuário sempre recebe preview antes de alterações de assets.
11. O núcleo geométrico e os solvers funcionam localmente. Provedores de IA externos são opcionais e controlados pelo broker existente.
12. CPU + Jobs/Burst é a primeira implementação de referência. Compute shaders entram somente depois da versão determinística estar coberta por testes.

## 3. Estado atual e lacunas

### 3.1 Capacidades reutilizáveis

- MCP com contratos, autenticação, registro de ferramentas e níveis de risco.
- Coder Chat com anexos de contexto e execução de ferramentas.
- `MutationScope` para Undo, assets, cenas e Prefab overrides.
- Resolução de `MeshFilter` e `SkinnedMeshRenderer` pelo `MeshResolver`.
- Análise de submeshes, planos, regiões suaves e arestas.
- Renderização multi-view com wireframe e overlays.
- Picking 2D para ponto, normal, triângulo, submesh e região 3D.
- Segmentação opcional no sidecar.
- Ferramentas opcionais de criação e validação ProBuilder.

### 3.2 Lacunas a implementar

- Documento de edição e cópia editável de meshes importadas.
- Topologia half-edge e identidade estável de elementos.
- Seleção de vértice, aresta, face e região na Scene View.
- Operações de modelagem e deformação.
- Preservação e transferência explícita de canais de skinning.
- Classificação semântica humanoide.
- Extração determinística de partes.
- Catálogo de partes compatíveis para personagens modulares.
- Fitting de roupas estático, com rig e multipose.
- Ferramentas MCP de preview, aplicação, validação e commit.

## 4. Objetivos e não objetivos

### 4.1 Objetivos

- Permitir edição útil de meshes estáticas e skinned sem sair do Unity.
- Tornar tarefas comuns de modelagem acessíveis a usuários pouco técnicos.
- Automatizar segmentação corporal e fitting de roupas com mínima ação.
- Preservar fidelidade dos assets e oferecer diagnósticos objetivos.
- Preparar uma API de alto nível para agentes e futuras APIs de geração 3D.
- Criar partes intercambiáveis de corpo, roupas, armaduras e acessórios.
- Manter compatibilidade com os fluxos existentes do pacote.

### 4.2 Não objetivos iniciais

- Substituir Blender, Maya ou ZBrush em todas as funções.
- Escultura multibilionária de polígonos.
- Retopologia cinematográfica automática na primeira versão.
- UV unwrap totalmente automático na primeira versão.
- Alterar o FBX original dentro da pasta importada.
- Permitir que um LLM aplique índices de faces arbitrários sem revisão de topologia.
- Tornar ProBuilder uma dependência obrigatória.

## 5. Experiência do usuário

### 5.1 Novos menus

Menu principal:

```text
Window
└── Asset Modeler (Unity MCP)

GameObject
└── Asset Modeler
    ├── Make Editable Copy
    ├── Open in Asset Modeler
    ├── Smart Body Split...
    ├── Smart Surface Fit...
    ├── Merge Selected Meshes...
    └── Validate Mesh

Assets
└── Asset Modeler
    ├── Make Editable Prefab Variant
    ├── Rebase From Source
    └── Export To FBX...
```

Atalhos devem ser configuráveis pelo Shortcut Manager, sem sobrescrever atalhos nativos do Unity.

### 5.2 Contexto da Scene View

O Asset Modeler registra um `EditorToolContext` com modos:

- Object
- Vertex
- Edge
- Face
- Region
- Bone/Anchor

Overlays:

- **Modeling Toolbar:** modo de seleção e operação ativa.
- **Tool Settings:** parâmetros, global/local/normal, snap e simetria.
- **Selection Info:** contagens, canais e região semântica.
- **Modifier Stack:** preview, ordem, habilitar, aplicar e remover.
- **Diagnostics:** degenerados, non-manifold, UV seams, pesos, penetrações e confiança semântica.
- **AI Assistant:** prompt curto, presets e estado do job.

### 5.3 Fluxo Smart Body Split com pouca ação

Fluxo padrão:

1. Usuário seleciona o root do personagem ou um `SkinnedMeshRenderer`/`MeshFilter`.
2. Abre **GameObject → Asset Modeler → Smart Body Split...**.
3. O sistema detecta automaticamente:
   - meshes envolvidas;
   - Animator e Avatar;
   - rig Humanoid, Generic ou ausência de rig;
   - materiais, blend shapes e submeshes;
   - orientação, escala e simetria aproximada.
4. O preset **Partes principais** já vem selecionado.
5. Opcionalmente, o usuário escreve: “separe cabeça, braços, mãos, peitoral, pernas e pés”.
6. A ferramenta mostra cores por parte e uma porcentagem de confiança.
7. Se a cobertura e confiança passarem os gates, o botão principal é **Criar partes**.
8. Uma confirmação gera os assets e um Prefab Variant modular.

Se houver ambiguidade, a UI solicita somente a menor correção possível:

- clicar numa região que pertence à cabeça;
- arrastar uma fronteira;
- escolher entre duas propostas;
- pintar incluir/excluir em poucos polígonos.

Não se deve obrigar o usuário a selecionar manualmente cada face.

### 5.4 Presets de split

- **Partes principais:** cabeça, tronco, braço esquerdo/direito, perna esquerda/direita.
- **Corpo modular:** cabeça, pescoço, peitoral, abdômen/quadril, braço, antebraço, mão, coxa, panturrilha e pé.
- **FPS:** cabeça, torso, braços e mãos, ocultando pernas opcionalmente.
- **Dano/desmembramento:** regiões principais com seam rings e pontos de encaixe.
- **Roupas:** tronco, mangas, pernas e acessórios conectados.
- **Por ossos:** grupos inferidos da influência dominante de ossos.
- **Por materiais/submeshes:** baseline determinístico sem IA.
- **Custom:** linguagem natural + editor de partes.

## 6. Arquitetura de software

```text
Editor/Modeling/
  Core/
    MeshEditDocument.cs
    MeshSnapshot.cs
    MeshChannelSet.cs
    StableElementId.cs
    HalfEdgeTopology.cs
    SelectionSet.cs
    MeshRevision.cs

  Assets/
    EditableMeshAsset.cs
    MeshSourceLink.cs
    MeshEditSessionAsset.cs
    EditableMeshFactory.cs
    MeshCommitService.cs

  Operations/
    IMeshOperation.cs
    Transform/
    Topology/
    Deform/
    SurfaceFit/
    SmartSplit/

  Spatial/
    Bvh.cs
    TriangleQueries.cs
    SignedDistanceField.cs
    SelfIntersection.cs

  Skinning/
    SkinChannelSnapshot.cs
    SkinChannelTransfer.cs
    BlendShapeTransfer.cs
    BindPoseValidator.cs
    HumanoidBoneMap.cs
    PoseSampler.cs

  Validation/
    MeshValidator.cs
    UvValidator.cs
    SkinValidator.cs
    IntersectionValidator.cs
    SplitFidelityValidator.cs

  Commands/
    ModelingCommandService.cs
    ModelingTransaction.cs

  SceneTools/
    AssetModelerContext.cs
    ElementSelectionTool.cs
    TransformElementsTool.cs
    BrushTool.cs
    RegionLinkTool.cs

  UI/
    AssetModelerWindow.cs
    ModelingToolbarOverlay.cs
    ModelingInspectorOverlay.cs
    ModifierStackPanel.cs
    DiagnosticsOverlay.cs
    SmartBodySplitWizard.cs

  Mcp/
    ModelingToolBootstrap.cs
    MeshEditSessionTools.cs
    SmartBodySplitTools.cs
    SurfaceFitTools.cs

Runtime/Modeling/
  BodyPartSet.cs
  BodyPartDefinition.cs
  ModularCharacterAssembler.cs
  SkeletonCompatibilitySignature.cs

Tests/Editor/Modeling/
Tests/Runtime/Modeling/
TestAssets~/Modeling/
```

Assemblies propostas:

- `Maicon.SceneAnalyzer.Editor.Modeling.Core`: geometria e contratos, sem UI e sem ProBuilder.
- `Maicon.SceneAnalyzer.Editor.Modeling`: UI, sessões, validação, operações e MCP.
- `Maicon.SceneAnalyzer.Editor.Modeling.ProBuilder`: adaptador opcional.
- `Maicon.SceneAnalyzer.Editor.Modeling.Fbx`: adaptador opcional do FBX Exporter.
- `Maicon.SceneAnalyzer.Runtime.Modeling`: somente catálogo e montagem de partes em runtime.
- assemblies de testes separados para Core, Editor, integrações e Runtime.

## 7. Modelo de dados central

### 7.1 `MeshEditDocument`

Campos mínimos:

```text
documentId
sessionId
sourceAssetGuid
sourceLocalFileId
sourceHash
sourceObjectGlobalId
workingMeshGuid
workingPrefabGuid
topologyRevision
geometryRevision
stableElementTable
channelManifest
submeshManifest
rendererBinding
skinBinding
namedSelections
semanticRegions
modifierStack
validationState
operationHistory
createdUtc
lastModifiedUtc
```

### 7.2 `MeshChannelSet`

Deve capturar e reconstruir:

- positions;
- normals;
- tangents;
- colors/colors32;
- UV0 até UV7, mantendo a dimensão original;
- índices e `SubMeshDescriptor`;
- index format 16/32 bits;
- bounds;
- `GetBonesPerVertex` + `GetAllBoneWeights`;
- bind poses;
- nomes, frames, pesos e deltas de blend shapes;
- atributos customizados compatíveis com `VertexAttributeDescriptor`;
- relação submesh → slots de material.

### 7.3 Identidade e revisões

- Vértices, arestas e faces recebem IDs estáveis próprios.
- Cada operação que muda conectividade incrementa `topologyRevision`.
- Operações apenas geométricas incrementam `geometryRevision`.
- Toda operação topológica retorna um `SelectionRemap`.
- Toda ferramenta MCP envia `expectedTopologyRevision`.
- Revisão divergente retorna `STALE_MESH_REVISION` e não altera o asset.

### 7.4 Sessão e modifier stack

A sessão contém operações serializáveis, ordenadas e reproduzíveis. Um preview é reconstruído a partir da mesh-base e da pilha ativa. Undo/Redo altera operações e parâmetros pequenos; não registra uma cópia completa da mesh a cada movimento do mouse.

O commit:

1. congela a revisão esperada;
2. valida a pilha;
3. materializa uma mesh temporária;
4. executa todos os validadores;
5. abre um `MutationScope`;
6. grava/substitui somente a mesh editável;
7. atualiza Prefab e manifesto;
8. fecha o Undo group;
9. retorna hashes e métricas.

## 8. Contrato das operações

```csharp
public interface IMeshOperation
{
    MeshOperationDescriptor Descriptor { get; }
    ValidationReport Validate(MeshOperationContext context);
    MeshPreviewResult Preview(MeshOperationContext context);
    MeshOperationResult Apply(MeshOperationContext context);
}
```

O descritor informa:

- se muda topologia;
- canais lidos e escritos;
- suporte a static/skinned/blend shapes;
- capacidade de remapear seleção;
- risco;
- custo estimado;
- se exige preview;
- se é cancelável;
- se aceita execução headless.

Todo resultado inclui:

```text
operationId
documentId
revisionBefore
revisionAfter
inputHash
resultHash
createdVertices/Edges/Faces
removedVertices/Edges/Faces
selectionRemap
channelPreservationReport
validationReport
warnings
elapsedMs
```

## 9. Smart Body Split

### 9.1 Requisitos funcionais

O Smart Body Split deve:

- aceitar `MeshFilter`, `SkinnedMeshRenderer`, root com vários renderers e Prefab;
- detectar Humanoid, Generic ou mesh sem rig;
- aceitar presets e linguagem natural;
- separar por partes anatômicas, materiais, submeshes, ossos ou regiões customizadas;
- mostrar preview colorido antes de gravar;
- cobrir 100% dos triângulos ou listar explicitamente `Unassigned`;
- preservar canais por vértice e por face;
- criar assets com nomes determinísticos e sem colisão;
- criar um Prefab Variant modular opcional;
- gerar um `BodyPartSet` opcional;
- permitir editar fronteiras sem reiniciar a análise;
- permitir recombinar partes originadas da mesma sessão;
- produzir relatório de fidelidade;
- suportar Undo/Redo e cancelamento.

### 9.2 Fontes de evidência semântica

Para Humanoid com rig, a ordem de confiança é:

1. mapeamento `HumanBodyBones` → transforms;
2. soma dos bone weights por grupos anatômicos;
3. hierarquia e nomes dos bones;
4. posição normalizada no espaço do personagem;
5. conectividade e distância geodésica;
6. simetria bilateral;
7. curvatura, concavidade e narrow rings;
8. imagens multi-view e IA visual, apenas como evidência adicional.

Para Generic rig:

1. hierarquia e nomes normalizados;
2. distribuição de bone weights;
3. topologia da árvore de bones;
4. pose e proporções;
5. landmarks sugeridos por IA quando necessário.

Para mesh sem rig:

1. orientação e simetria estimadas;
2. skeletonização geométrica aproximada;
3. componentes e geodésica;
4. multi-view render + landmarks visuais;
5. sementes projetadas na mesh pelo picking;
6. refinamento por grafo.

Quando não houver evidência suficiente, a ferramenta não inventa uma fronteira silenciosamente; ela solicita um clique-semente ou apresenta alternativas.

### 9.3 Intenção tipada

A linguagem natural é convertida para `BodyPartSplitIntent`:

```json
{
  "preset": "modular_body",
  "parts": [
    { "id": "head", "label": "Cabeça", "symmetry": "center" },
    { "id": "chest", "label": "Peitoral", "symmetry": "center" },
    { "id": "upper_arm", "side": "left" },
    { "id": "upper_arm", "side": "right" },
    { "id": "leg", "side": "left" },
    { "id": "leg", "side": "right" }
  ],
  "boundaryMode": "anatomical_joint",
  "preserveFullSkeleton": true,
  "createPrefabVariant": true,
  "createBodyPartSet": true,
  "capOpenBoundaries": false,
  "unassignedPolicy": "block"
}
```

O LLM só pode gerar esse contrato. IDs de faces são produzidos pelo classificador local e associados à revisão da mesh.

### 9.4 Classificação inicial por rig

Para cada vértice:

1. ler todas as influências com `BoneWeight1`;
2. mapear cada bone a um grupo anatômico;
3. somar pesos por grupo;
4. propagar as probabilidades para os triângulos;
5. aplicar penalidade quando os vértices do triângulo discordam fortemente;
6. manter as duas melhores classes e margem de confiança.

Para cada face, o custo de pertencer a uma parte combina:

```text
boneWeightCost
landmarkDistanceCost
normalizedPositionCost
geodesicCost
symmetryCost
materialPriorCost
visualSemanticCost
```

### 9.5 Refinamento de fronteira

Construir um grafo dual de faces. Faces adjacentes são conectadas por um custo de corte baseado em:

- ângulo diedral;
- curvatura;
- comprimento da aresta;
- concavidade;
- proximidade de articulação;
- UV seam;
- material boundary;
- mudança de influência de bones;
- região protegida ou pintada pelo usuário.

Executar otimização multi-label, como alpha-expansion/graph cut ou equivalente determinístico. O refinamento deve:

- favorecer fronteiras curtas e estáveis;
- respeitar sementes de alta confiança;
- manter esquerda/direita coerentes;
- evitar ilhas minúsculas;
- impedir troca entre membros próximos;
- manter conectividade principal de cada parte, salvo quando o preset permitir múltiplos componentes.

### 9.6 Fronteiras anatômicas

Quando houver rig, articulações definem planos e anéis candidatos:

- cabeça/pescoço;
- ombro/braço;
- cotovelo/antebraço;
- punho/mão;
- peito/abdômen;
- quadril/coxa;
- joelho/panturrilha;
- tornozelo/pé.

O plano não deve simplesmente cortar no pivot do bone. A fronteira final deve procurar um narrow ring próximo, ponderado pela influência dos bones e pela curvatura. O usuário pode ajustar um offset ao longo do eixo do membro.

### 9.7 Extração determinística das partes

Depois da proposta aprovada:

1. congelar `sourceHash` e `topologyRevision`;
2. atribuir exatamente uma parte a cada triângulo, salvo modo de sobreposição explícito;
3. iterar por submesh e parte;
4. criar tabela `sourceVertexIndex → destinationVertexIndex` por parte;
5. copiar os vértices referenciados;
6. copiar todos os canais pelo índice de origem;
7. reconstruir índices preservando winding;
8. reconstruir submeshes e slots de material;
9. copiar bone weights sem truncar influências;
10. copiar bind poses e bones do renderer;
11. copiar todos os frames de blend shape para os vértices incluídos;
12. manter normais/tangentes originais nas costuras;
13. recalcular apenas bounds por padrão;
14. validar e somente então criar assets.

Faces nunca são cortadas no MVP. Como a classificação é por triângulo, uma fronteira usa as arestas existentes. O modo futuro **Refine Boundary** pode inserir novos vértices, mas exigirá interpolação completa de canais.

### 9.8 Preservação de skinning

Com `preserveFullSkeleton = true`:

- copiar o array completo `bones` do `SkinnedMeshRenderer`;
- copiar `rootBone`;
- copiar todas as bind poses, na mesma ordem;
- copiar `localBounds`, quality e opções relevantes do renderer;
- copiar todos os pesos não zero de cada vértice;
- manter índices de bones inalterados;
- normalizar apenas se o original estiver fora da tolerância e o usuário autorizar reparo.

Com `pruneUnusedBones = true`:

- calcular bones usados pela parte;
- incluir ancestrais necessários até o root;
- criar mapa oldBoneIndex → newBoneIndex;
- remapear todos os `BoneWeight1`;
- remapear bind poses;
- provar equivalência do skinning em um conjunto de poses antes do commit.

O modo de preservação completa é o padrão porque simplifica a intercambialidade e reduz risco.

### 9.9 Blend shapes

Para cada blend shape e frame:

- preservar nome e peso do frame;
- copiar `deltaVertices`, `deltaNormals` e `deltaTangents` pelos índices de origem;
- adicionar frames na mesma ordem;
- manter deltas idênticos em vértices duplicados nas costuras;
- comparar o resultado deformado com a fonte em pesos 0%, 50% e 100%, ou nos pesos reais dos frames.

Caso uma operação topológica futura crie vértices, os deltas serão interpolados baricentricamente e o relatório marcará `interpolated`, nunca `exact`.

### 9.10 Materiais e submeshes

Cada triângulo mantém seu submesh original. Há dois modos:

- **Preserve slots:** mantém os índices e materiais completos, inclusive slots vazios na parte.
- **Compact slots:** remove slots não usados e cria mapa de origem para destino.

O padrão para parts catalog é compactar slots e registrar o mapa no manifesto.

### 9.11 Costuras

O split padrão cria fronteiras abertas. Isso é correto para partes intercambiáveis e preserva a superfície original.

Opções futuras:

- gerar cap planar;
- gerar cap seguindo um seam ring;
- criar faixa de sobreposição oculta;
- criar sockets/anchors no centro e orientação da costura;
- gerar skirt de transição;
- marcar material exclusivo da superfície de corte.

Caps alteram topologia e exigem preview separado. Nunca serão ativados silenciosamente.

### 9.12 Resultado e manifesto

Cada execução cria um `SplitArtifactManifest`:

```text
splitId
sourceAssetGuid/sourceHash
sourceObjectGlobalId
intentHash
proposalHash
topologyRevision
part assets
part face/vertex counts
source-to-destination maps
material slot maps
skeleton signature
bind pose hash
blend shape manifest
seam signatures
confidence metrics
validation report
tool/package version
createdUtc
```

O manifesto permite validar, recombinar, comparar e reproduzir o split.

## 10. Personagens modulares

### 10.1 `BodyPartSet`

`BodyPartSet` é um `ScriptableObject` com:

- skeleton compatibility signature;
- rig type Humanoid/Generic/None;
- lista de slots;
- mesh/prefab de cada parte;
- seam signature;
- material bindings;
- blend shape compatibility;
- origem e versão;
- regras de substituição.

Slots padrão:

```text
Head
Neck
Chest
AbdomenHip
UpperArm.L/R
LowerArm.L/R
Hand.L/R
UpperLeg.L/R
LowerLeg.L/R
Foot.L/R
```

### 10.2 Compatibilidade

Uma parte é compatível quando:

- skeleton hierarchy hash é igual ou existe remapeamento aprovado;
- bind poses estão dentro da tolerância;
- seam signature corresponde;
- orientação e escala são compatíveis;
- blend shapes exigidos existem ou têm fallback;
- materiais obrigatórios podem ser resolvidos.

### 10.3 Runtime

`ModularCharacterAssembler` deve:

- instanciar partes como `SkinnedMeshRenderer` separados compartilhando o skeleton;
- trocar partes por slot;
- validar compatibilidade antes da troca;
- manter materiais e blend shape weights compartilháveis;
- oferecer bake/combinação opcional para build, sem destruir os assets originais.

O primeiro release pode limitar-se à montagem por renderers separados. Combinação otimizada em runtime é uma fase posterior.

## 11. Catálogo completo de ferramentas do Asset Modeler

### 11.1 Posicionamento

- move/rotate/scale em global, local e normal;
- snap em grid, vértice, midpoint, aresta, face, superfície, bone e socket;
- align centers/min/max;
- distribute;
- center pivot e pivot por seleção;
- place on surface;
- fit accessory to landmarks;
- parent/unparent preservando world transform.

### 11.2 Layout e repetição

- mirror com ou sem cópia;
- weld no plano de simetria;
- array linear;
- array radial;
- grid array;
- fit count between endpoints;
- duplicação encostada pelo bounds;
- presets salvos por usuário.

### 11.3 Limpeza e combinação

- remover triângulos degenerados;
- remover vértices/índices não usados;
- weld por tolerância;
- detectar duplicados;
- dissolver arestas coplanares quando seguro;
- corrigir winding e normais;
- localizar buracos e non-manifold;
- merge de meshes mantendo ou compactando materiais;
- split por connected components, materiais, submeshes, regiões e semântica.

### 11.4 Topologia

- extrude faces/edges;
- inset;
- bevel/chamfer;
- bridge loops;
- fill hole;
- subdivide;
- triangulate/retriangulate;
- adaptive subdivision;
- boundary refinement;
- decimation preservando bordas e UV seams em fase posterior.

### 11.5 Deformação

- soft selection;
- grab/smooth/relax/flatten/inflate;
- bend/twist/taper;
- lattice/FFD;
- spline-guided deform;
- Laplacian;
- ARAP;
- shrinkwrap;
- smart surface fit;
- máscaras por região e rigidez.

## 12. Smart Surface Fit e roupas

Pipeline:

1. alinhar fonte e alvo por pivô, bounds, landmarks ou bones;
2. segmentar regiões correspondentes;
3. identificar área de influência;
4. subdividir adaptativamente somente onde a densidade é insuficiente;
5. construir correspondências por distância, normal, geodésica, região e bone;
6. resolver Laplacian + ARAP com anchors e rigidez;
7. aplicar distância mínima/espessura;
8. resolver colisão com corpo e auto-interseção;
9. validar em uma ou múltiplas poses;
10. produzir preview e relatório.

O fitting multipose constrói um envelope a partir de clips/poses escolhidos. Uma roupa não deve ser aprovada apenas porque não penetra na T-pose.

## 13. MCP e IA

### 13.1 Ferramentas de sessão

```text
mesh_begin_edit_session
mesh_get_edit_context
mesh_rebase_edit_session
mesh_discard_edit_session
mesh_commit_edit_session
```

### 13.2 Seleção e regiões

```text
mesh_get_selection
mesh_select_elements
mesh_select_region
mesh_link_regions
mesh_paint_region_constraint
```

### 13.3 Operações

```text
mesh_preview_operation
mesh_apply_operation
mesh_reorder_modifier
mesh_remove_modifier
mesh_validate
```

### 13.4 Smart Body Split

```text
mesh_analyze_body_parts
mesh_get_split_proposal
mesh_preview_smart_split
mesh_refine_split_boundary
mesh_apply_smart_split
mesh_validate_split
mesh_recombine_split
```

### 13.5 Surface Fit

```text
mesh_preview_surface_fit
mesh_apply_surface_fit
mesh_evaluate_pose_clearance
```

### 13.6 Exportação

```text
mesh_export_fbx
mesh_create_modular_character_prefab
```

### 13.7 Contrato de segurança

Ferramentas de escrita incluem:

```text
sessionId
documentId
operationId
expectedTopologyRevision
expectedSourceHash
previewHash
target reference
options
```

Regras:

- `previewHash` é obrigatório para split, merge, operações topológicas e fitting.
- `mesh_apply_smart_split` usa `AssetWrite | Destructive`, risco alto e confirmação.
- `mesh_analyze_body_parts` é read-only e pode ser long-running.
- resultados grandes permanecem em cache local; MCP recebe IDs, hashes, métricas e amostras.
- nenhum segredo de provedor é salvo em assets Unity.
- chamadas remotas precisam ser opt-in e indicar quais dados saem da máquina.

## 14. Estratégia de IA

### 14.1 Responsabilidades adequadas para IA

- interpretar linguagem natural;
- escolher preset e granularidade;
- sugerir mapeamento de bones Generic;
- detectar landmarks em renders;
- nomear regiões e partes;
- explicar baixa confiança;
- sugerir correções e parâmetros;
- orquestrar análise, preview, validação e commit.

### 14.2 Responsabilidades proibidas para IA

- enviar índices de faces sem `proposalHash` e revisão;
- editar diretamente o `sharedMesh` importado;
- descartar bone weights, UVs ou blend shapes;
- confirmar uma própria operação de alto risco sem política autorizada;
- declarar sucesso sem executar os validadores;
- transmitir a mesh a serviços externos sem consentimento.

### 14.3 Sidecar

O sidecar atual deve ganhar métodos separados dos classificadores arquitetônicos:

```text
humanoid_parts
generic_rig_parts
unrigged_character_parts
semantic_mesh_regions
```

O contrato de retorno deve usar probabilidades/region IDs, não apenas labels. A execução determinística do split permanece no C# do Editor.

O caminho local por rig deve funcionar sem sidecar. O sidecar e a visão são complementos para Generic/sem rig e casos ambíguos.

## 15. Performance e concorrência

Metas iniciais, medidas em hardware de referência registrado nos benchmarks:

- hover/picking com BVH aquecido: p95 ≤ 16 ms;
- seleção por caixa em 100 mil triângulos: p95 ≤ 100 ms;
- preview de operações simples em 100 mil triângulos: ≤ 1 s;
- primeira proposta de split rigged em 100 mil triângulos: ≤ 2 s local;
- interação de brush em região limitada: p95 ≤ 33 ms;
- primeiro preview progressivo de solver: ≤ 500 ms quando possível;
- todas as tarefas acima de 250 ms exibem progresso e aceitam cancelamento.

Regras:

- Unity API e aplicação final na main thread;
- cálculo puro em Jobs/tasks seguros;
- resultados só são aplicados se documento e revisão ainda coincidirem;
- caches indexados por source hash + topology revision;
- ROI para brushes e deformadores;
- limite configurável de memória de preview;
- nenhuma alocação de milhões de objetos C# por elemento; usar NativeArray/estruturas compactas.

## 16. Validação

### 16.1 Invariantes gerais

- nenhum índice fora do intervalo;
- nenhum NaN/Infinity;
- winding consistente conforme política;
- tamanhos dos canais correspondem ao vertex count;
- submesh descriptors válidos;
- bounds válidos;
- nenhuma perda de canal não autorizada;
- source FBX inalterado;
- hashes e revisões coerentes.

### 16.2 Invariantes do split

- cada triângulo-fonte aparece exatamente uma vez no modo sem sobreposição;
- soma de triângulos das partes = triângulos da fonte;
- a união das partes reconstrói posições e índices da fonte, considerando duplicação de fronteira;
- valores de canais copiados são bitwise iguais quando o modo é `exact`;
- pesos por vértice somam dentro da tolerância da fonte;
- bone indices são válidos;
- bind pose count corresponde ao array de bones;
- todos os blend shapes e frames esperados existem;
- materiais e submeshes têm mapa completo;
- nenhuma parte vazia é criada;
- partes principais respeitam conectividade e lado;
- 100% das faces estão atribuídas ou explicitamente bloqueadas como `Unassigned`.

### 16.3 Fidelidade em animação

Comparar fonte e união visual das partes em:

- bind pose;
- T-pose/configuração de Avatar;
- conjunto de poses extremas;
- amostras de clips configurados;
- blend shapes em pesos representativos.

Métricas:

- erro máximo/RMS de posição de vértices correspondentes;
- diferença angular de normais/tangentes;
- diferença de silhouette por render;
- gaps nas costuras;
- mudança de bounds;
- clipping e auto-interseção.

O split exato, sem capping ou mudança topológica, deve ter erro geométrico zero dentro da precisão float usada pela fonte.

## 17. Testes

### 17.1 Fixtures

- plano, cubo e cilindro;
- mesh com buracos, duplicados e non-manifold;
- multi-submesh/múltiplos materiais;
- UV seams e hard normals;
- escala negativa;
- índices UInt16 e UInt32;
- mais de 65 mil vértices;
- SkinnedMesh com 1, 2, 4 e mais de 4 influências;
- rig Humanoid;
- rig Generic com nomes não padronizados;
- personagem sem rig;
- blend shapes com um e vários frames;
- corpo com partes desconectadas;
- mãos/dedos próximos ao torso;
- duas pernas encostadas;
- personagem assimétrico;
- múltiplos SkinnedMeshRenderers no mesmo root.

### 17.2 Unitários

- half-edge topology;
- IDs estáveis e remapeamento;
- cópia de canais;
- mapeamento de bones;
- classificação por pesos;
- graph refinement;
- extração por parte;
- material remap;
- blend shape extraction;
- skeleton signature;
- manifesto e hashes;
- cancelamento e revisão obsoleta.

### 17.3 EditMode

- criação de cópia editável;
- FBX original inalterado;
- Undo/Redo de sessão e commit;
- Prefab Variant e overrides;
- menu/contexto/Scene View;
- split estático e skinned;
- recombinação;
- reimport/source drift;
- schemas MCP e descritores explícitos;
- preview/apply parity.

### 17.4 Runtime

- troca de partes por slot;
- compartilhamento de skeleton;
- validação de compatibilidade;
- animação sem gaps;
- propagação de blend shape weights;
- serialização de `BodyPartSet`.

### 17.5 Matriz

- Unity 6000.1, 6000.2 e 6000.3;
- Built-in, URP e HDRP;
- ProBuilder ausente/presente;
- FBX Exporter ausente/presente;
- Embedded e Broker;
- domain reload;
- batch/EditMode tests onde aplicável;
- Windows como gate inicial; macOS/Linux em release posterior ou CI disponível.

## 18. Observabilidade e diagnósticos

Cada job registra:

- job/session/document/operation IDs;
- source e topology hashes;
- quantidade de elementos;
- estágios e tempos;
- uso aproximado de memória;
- cancelamento;
- confiança por parte;
- validações e falhas;
- assets criados;
- versão do algoritmo/modelo opcional.

A UI deve oferecer **Copy diagnostic report**, sem incluir a geometria completa ou segredos.

## 19. Backup, migração e recuperação

Antes da primeira implementação:

1. confirmar worktree e preservar mudanças concorrentes;
2. criar tag do baseline aprovada;
3. gerar arquivo externo verificável do pacote atual;
4. registrar commit, package version e checksums;
5. provar restauração e compilação;
6. desenvolver em branch dedicada.

Durante a edição:

- sessões temporárias nunca substituem o FBX;
- commits usam asset temporário + troca controlada;
- interrupção durante cálculo não modifica asset;
- falha durante commit mantém a última versão válida;
- `MeshEditSessionAsset` pode recuperar previews após domain reload;
- reimport do FBX marca a sessão como `SOURCE_DRIFTED` e exige rebase ou descarte.

## 20. Fases de entrega

### Fase 0 — baseline e contratos

- backup verificável;
- fixtures e benchmarks;
- asmdefs;
- contratos de operação, revisão e canais;
- gates estáticos e EditMode.

Gate: nenhuma alteração funcional, baseline restaurável e testes das fixtures executáveis.

### Fase 1 — documento editável

- `MeshEditDocument`;
- cópia `.asset`/Prefab Variant;
- sessões e modifier stack;
- capture/rebuild de todos os canais;
- validação e commit;
- MCP de sessão/read-only.

Gate: round-trip sem operação produz mesh equivalente e não altera FBX.

### Fase 2 — Scene View e operações seguras

- context, overlays e seleção;
- snap e gizmos;
- mirror, array, align e pivot;
- cleanup, connected components e merge;
- MCP usando o mesmo command service.

Gate: UI/MCP têm paridade e Undo/Redo confiável.

### Fase 3 — Smart Body Split MVP

- Humanoid rig mapping;
- preset partes principais e corpo modular;
- classificação por pesos e grafo;
- preview colorido;
- extração exata preservando rig/blend shapes;
- manifesto e `BodyPartSet`;
- criação de Prefab Variant modular;
- MCP analyze/preview/apply/validate.

Gate: fixtures Humanoid recuperam 100% das faces, preservam canais e animam sem gaps.

### Fase 4 — Smart Body Split avançado

- Generic rig inference;
- mesh sem rig com multi-view/landmarks;
- editor de fronteiras;
- presets FPS/dano/roupas;
- seam anchors e compatibilidade;
- recombinação;
- pruning opcional de bones comprovado por poses.

Gate: casos ambíguos falham com orientação útil ou pedem apenas sementes mínimas.

### Fase 5 — modelagem topológica

- half-edge completa;
- weld, extrude, inset, bridge, bevel;
- subdivisão e boundary refinement;
- adaptador ProBuilder opcional;
- transferência de canais para vértices novos.

Gate: nenhuma operação aprovada perde canais e selection remap passa nos testes.

### Fase 6 — deformação

- soft selection e brushes;
- bend/twist/taper;
- lattice;
- Laplacian;
- ARAP;
- cancelamento, ROI e preview progressivo.

Gate: solvers determinísticos, sem NaN e dentro dos budgets definidos.

### Fase 7 — Smart Surface Fit

- landmarks e regions;
- BVH/SDF;
- adaptive tessellation;
- distância/espessura;
- colisão corporal e auto-interseção;
- static fit;
- rig-aware/multipose fit.

Gate: roupas mantêm clearance configurado no conjunto de poses e reportam exceções.

### Fase 8 — IA e ecossistema

- sidecar humanoide/Generic/unrigged;
- fluxo completo pelo Coder Chat;
- staging de assets vindos de APIs 3D;
- validação automática antes da aceitação;
- exportador FBX opcional e relatório de limitações;
- documentação e exemplos.

Gate: agente executa inspect → preview → validate → confirm → commit sem caminho de mutação paralelo.

## 21. Critérios de release 1.0

- fonte importada nunca é modificada;
- todos os writes relevantes têm Undo ou commit transacional recuperável;
- UI e MCP compartilham operações;
- revisões obsoletas são rejeitadas;
- todos os canais têm política explícita de preserve/interpolate/recalculate/drop;
- nenhum `drop` ocorre sem confirmação específica;
- Smart Body Split Humanoid e Generic passam fixtures e assets reais;
- split exato tem fidelidade geométrica comprovada;
- fitting multipose tem métricas de clearance e interseção;
- domain reload e cancelamento não corrompem sessões;
- performance atende budgets publicados;
- optional packages ausentes degradam funcionalidade com mensagem clara;
- documentação operacional e troubleshooting concluídos;
- validação ao vivo no Unity executada antes de declarar a versão pronta.

## 22. Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Classificação errada de membros próximos | bone priors, geodésica, simetria, conectividade e preview obrigatório |
| Perda de pesos ou blend shapes | captura completa de canais, cópia por índice e validator de fidelidade |
| Costura visível depois do split | preservar posições/normais/tangentes e compartilhar o mesmo skeleton |
| Rig Generic sem nomes úteis | inferência por hierarquia/pesos + landmarks mínimos |
| Mesh sem rig ambígua | multi-view + sementes; nunca aplicar com baixa confiança |
| Undo consumindo memória | stack de operações e snapshot somente no commit |
| Reimport invalidando o trabalho | source hash, estado `SOURCE_DRIFTED` e rebase explícito |
| ProBuilder incompatível com skinned meshes | núcleo independente e adaptador opcional |
| IA produzir plano obsoleto | topology revision + proposal/preview hashes |
| FBX Exporter perder dados | validar capacidades/limitações e manter `.asset`/Prefab como fonte autoritativa |
| Solvers lentos | ROI, multi-resolução, Jobs/Burst, progresso e cancelamento |

## 23. Dependências e referências oficiais

- Unity custom Editor tools: <https://docs.unity3d.com/6000.1/Documentation/Manual/UsingCustomEditorTools.html>
- Unity custom overlays: <https://docs.unity3d.com/6000.1/Documentation/Manual/overlays-custom.html>
- Mesh data: <https://docs.unity3d.com/2023.2/Documentation/Manual/AnatomyofaMesh.html>
- Mesh writable data: <https://docs.unity3d.com/6000.1/Documentation/ScriptReference/Mesh.AllocateWritableMeshData.html>
- Bone weights: <https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Mesh.GetAllBoneWeights.html>
- Blend shape frame data: <https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Mesh.GetBlendShapeFrameVertices.html>
- Undo: <https://docs.unity3d.com/6000.1/Documentation/ScriptReference/Undo.RecordObject.html>
- FBX Exporter: <https://docs.unity3d.com/Packages/com.unity.formats.fbx@latest/>

O FBX Exporter é opcional e deve ser tratado como integração com limitações próprias. O formato autoritativo de edição continuará sendo a mesh `.asset` e o Prefab/Prefab Variant do Unity.

## 24. Definição do primeiro corte vertical

O primeiro corte implementável de ponta a ponta será:

1. fixture Humanoid skinned com materiais e blend shape;
2. **Make Editable Copy**;
3. `MeshEditDocument` com snapshot completo;
4. preset **Partes principais**;
5. classificação por bone weights;
6. preview colorido na Scene View;
7. confirmação;
8. criação de cabeça, tronco, braços e pernas como meshes separadas;
9. renderers compartilhando o skeleton original;
10. relatório de canais e fidelidade;
11. Undo;
12. as mesmas ações por MCP;
13. teste de animação comparando a fonte com a união das partes.

Esse corte valida a arquitetura decisiva antes de introduzir classificação visual, Generic rig, ausência de rig, capping, deformadores ou APIs externas de IA.
