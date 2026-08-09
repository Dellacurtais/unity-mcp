# Rig & Animation Studio — especificação e acompanhamento das fases 1–7

Atualizado em 2026-08-09. Este documento é a fonte de acompanhamento da evolução de rig e animação do `com.maicon.scene-analyzer`. A implementação partiu do checkpoint `3d766956627816384c5f80e8e5cd1db0cea689ae` (`0.18.1`), preservado na branch `backup/rig-animation-before-seven-phases-20260808`. Antes das revisões foram criados `rig-animation-before-corrections-20260808-234204.zip` (SHA-256 `36C46393EEF451755D03F3E327786061246BC09049C4C96675452577ED95EBB2`) e `before-clip-painter-animation-rigging-20260809-175038.zip` (SHA-256 `FA2C29AC513761707CA1B221A17992F58116B5E74155FD4C68B7CB46C3A6EB2D`). O contrato corrente é `0.18.2`.

## 1. Objetivo

Entregar dentro da Unity um editor visual e transacional para diagnosticar, criar, editar, transferir, retargetear, animar e validar rigs, mantendo o mesmo princípio do Asset Modeler:

```text
inspecionar → propor/preview → validar → aplicar → desfazer ou salvar
```

O sistema deve atender Humanoid, Generic e meshes ainda sem rig. A automação por IA pode sugerir mapas, landmarks e parâmetros, mas nunca aplica uma mutação destrutiva sem revisão e confirmação.

## 2. Decisões de produto

1. A interface será uma janela separada: `Window > Asset Modeler > Rig & Animation Studio`.
2. As sete áreas terão páginas próprias; controles de pesos, clips e auto-rig não serão misturados numa tela única.
3. A Scene View é a superfície principal para bones, heatmaps, pincéis, IK e preview de poses.
4. FBX e meshes importadas permanecem somente leitura. Edição de skin começa numa cópia `.asset` controlada.
5. Todos os bone weights são lidos e escritos com `GetBonesPerVertex`, `GetAllBoneWeights` e `SetBoneWeights`; não há truncamento implícito para quatro influências.
6. Operações persistentes têm Undo, validação antes/depois e relatório estruturado.
7. UI e MCP chamam os mesmos serviços. Não haverá uma segunda implementação para agentes.
8. `com.unity.animation.rigging` é integração opcional. O pacote continua compilando e oferece IK/bake nativo quando a dependência não estiver instalada.
9. Humanoid usa `Animator`/`Avatar`/`HumanTrait`; Generic usa caminhos, nomes normalizados, hierarquia, posição e orientação.
10. Assistência por IA é opt-in e produz propostas tipadas com hash. A aplicação continua determinística e local.

## 3. Estado inicial comprovado

- Animator/Mecanim: leitura e criação de controllers, states, transitions, parameters, layers, Blend Trees, clips, curvas, masks e events.
- Smart Body Split: preservação de skeleton, pesos, bind poses e blend shapes.
- `SplitPoseFidelityValidator`: comparação da fonte e das partes em pose atual ou `AnimationClip`.
- `SurfaceFitPoseEvaluator`: avaliação de clearance ao longo de clips.
- Bone Mode: seleção dos vértices dominados por um bone.
- `ModularCharacterAssembler`: troca básica de partes usando um skeleton compartilhado.

Essas capacidades são fundação, não substituem um editor de rig ou animação.

## 4. Arquitetura alvo

```text
Runtime/Modeling/
  RigPoseLibrary.cs
  RigRetargetProfile.cs
  RigAutoRigProfile.cs
  RigIkBinding.cs
  RigBindPoseSnapshot.cs

Editor/Modeling/Rigging/
  RigDiagnostics.cs
  RigClipValidation.cs
  SkinWeightEditing.cs
  SkeletonAndWeightTransfer.cs
  AnimationClipAndPoseStudio.cs
  RigRetargeting.cs
  RigIkAndBake.cs
  AutoRigging.cs
  RigAnimationStudioWindow.cs
  RigAnimationTools.cs
```

Arquivos podem ser consolidados quando isso reduzir ruído, desde que as responsabilidades permaneçam separadas.

## 5. Contratos comuns

### 5.1 `RigTargetContext`

Deve resolver e expor:

- root do personagem;
- `Animator`, `Avatar` e tipo `Humanoid`/`Generic`/`None`;
- um ou vários `SkinnedMeshRenderer`;
- root bone e bones por renderer;
- paths relativos estáveis;
- meshes e caminhos de assets;
- hash de hierarquia, bind poses e skin weights.

### 5.2 Segurança de assets

- `.fbx`, `.dae`, `.obj` e sub-assets importados são protegidos.
- Escritas exigem mesh `.asset` independente sob `Assets/`.
- O botão `Criar cópia editável de skin` duplica todos os canais e reassocia o renderer com Undo.
- Toda aplicação verifica se target, mesh e hashes ainda correspondem ao preview.

### 5.3 Resultados estruturados

Toda operação retorna:

- código e severidade;
- target e asset envolvidos;
- hash/revisão antes e depois;
- contagens afetadas;
- warnings e erros;
- métricas específicas da fase;
- indicação de Undo e assets criados.

## 6. Acompanhamento

| Fase | Estado | Gate principal |
|---|---|---|
| 1. Rig Inspector, heatmap e validação por clips | Automação reaprovada em 2026-08-09; validação visual pendente | diagnóstico determinístico, overlay e comparação de clips sem mutação |
| 2. Skin Weight Painter | Automação e índice espacial aprovados em 2026-08-09; validação visual de produção pendente | paint/smooth/mirror/normalize/prune com Undo e preservação de influências |
| 3. Bind Pose/Skeleton Editor e transferência | Automação reaprovada em 2026-08-09; validação visual pendente | rebind/transfer validado sem modificar importados |
| 4. Animation Clip Studio e Pose Library | Automação e matemática de mirror/additive aprovadas em 2026-08-09; validação visual de produção pendente | captura/aplicação de poses e operações de clip com Undo |
| 5. Retarget Humanoid/Generic | Automação reaprovada em 2026-08-09; validação visual pendente | mapa revisável e bake com erro mensurável |
| 6. Animation Rigging, IK e bake | Integração 1.4.1 e smoke visual isolado aprovados em 2026-08-09; validação no projeto de produção pendente | IK visual e clip baked reproduzindo o preview |
| 7. Auto-rig e assistência por IA | Automação reaprovada em 2026-08-09; validação visual pendente | proposta hashada, preview e aplicação confirmada |

Estados válidos: `Pendente`, `Em andamento`, `Automação aprovada`, `Validação visual pendente`, `Concluída`.

## 7. Fase 1 — Rig Inspector, heatmap e validação por clips

### Escopo

- Resolver personagem por root, renderer ou bone selecionado.
- Árvore de bones com filtro e foco por duplo clique.
- Diagnósticos:
  - root bone ausente;
  - bones nulos, repetidos ou fora do root;
  - bind pose count divergente;
  - pesos com bone index inválido;
  - pesos vazios, não finitos ou fora da tolerância de normalização;
  - quantidade máxima/média de influências;
  - vértices sem influência;
  - Avatar inválido ou mapeamento Humanoid incompleto;
  - escalas negativas/não uniformes na hierarquia;
  - bounds incapazes de conter a mesh animada.
- Heatmap por bone: influência total por vértice, gradiente, legendas e wireframe opcional.
- Preview de clip por slider de tempo usando `AnimationMode`.
- Validação batch de clips:
  - gaps/erro máximo e RMS do Smart Body Split;
  - bounds por amostra;
  - pesos/blend shapes válidos;
  - relatório agregado exportável.
- MCP: `rig_inspect`, `rig_validate_clips`.

### Gate

- Nenhum diagnóstico altera a cena ou os assets.
- Fixtures Legacy 4 influences e `BoneWeight1` com mais de 4 influências retornam as mesmas somas.
- O sampler sempre restaura a pose, inclusive em exceção.
- UI, serviço e MCP produzem o mesmo relatório serializado.

## 8. Fase 2 — Skin Weight Painter

### Escopo

- Criar cópia editável da mesh skinned.
- Modos `Replace`, `Add`, `Subtract`, `Smooth`, `Normalize`, `Prune` e `Flood`.
- Radius, strength, falloff, front faces/x-ray e limite de influências configuráveis.
- Lock de bones e vértices.
- Mirror X com mapa geométrico tolerante.
- Heatmap atualizado durante o stroke.
- Transferência parcial por região.
- Preview do stroke; commit no mouse-up; cancelamento por Escape.
- Undo por stroke e validação de todas as somas/índices.
- MCP: `rig_create_editable_skin`, `rig_apply_weight_operation`.

### Gate

- Nenhum vértice perde influência sem política explícita.
- Pesos permanecem finitos, não negativos e normalizados.
- Undo restaura bitwise a distribuição anterior.
- Mesh importada nunca é modificada.

## 9. Fase 3 — Bind Pose/Skeleton Editor e transferência

### Escopo

- Criar, renomear, duplicar, mover e reparentear bones com Undo.
- Definir root bone e reassociar renderer.
- Recalcular bind poses por `bone.worldToLocalMatrix * renderer.localToWorldMatrix`.
- Capturar/restaurar bind pose asset-side.
- Remover bones sem uso somente após prova em clips.
- Transferir pesos entre meshes usando:
  - correspondência de vertex/index quando disponível;
  - nearest surface/barycentric;
  - nomes/path map de bones;
  - regiões e simetria.
- Relatório de cobertura, distância e influências sem mapa.
- MCP: `rig_rebuild_bindposes`, `rig_transfer_weights`, `rig_edit_skeleton`.

### Gate

- Rebind na bind pose mantém a geometria mundial dentro da tolerância.
- Transferência cobre 100% dos vértices ou falha com lista explícita.
- Todos os bone indices resolvem no renderer destino.

## 10. Fase 4 — Animation Clip Studio e Pose Library

### Escopo

- Criar/duplicar clips standalone `.anim`.
- Timeline simples de sample, play, loop e keyframe.
- Capturar pose de transforms e músculos Humanoid.
- `RigPoseLibrary` com thumbnails/metadados opcionais.
- Aplicar, renomear, duplicar e excluir poses com Undo.
- Operações de clip:
  - trim;
  - scale de tempo;
  - reverse;
  - mirror;
  - loop closure;
  - root motion extract/remove;
  - additive reference;
  - merge sequencial;
  - curve copy/remap.
- Manter events e object-reference curves segundo política explícita.
- Mirror troca paths Generic e propriedades musculares Humanoid; eixos centrais `Left-Right`, `RootT.x` e `RootQ.y/z` recebem o sinal correto.
- Additive de rotações quaternion usa `inverse(reference) * source`, normalização e continuidade de hemisfério; curvas escalares continuam usando delta numérico.
- MCP: `animation_capture_pose`, `animation_apply_pose`, `animation_transform_clip`.

### Gate

- Fonte importada continua inalterada; saída sempre é `.anim` independente.
- Curvas, tangentes e events sobrevivem a operações não destrutivas.
- Quaternions aditivos recompõem a rotação-fonte dentro da tolerância e mirror Humanoid mantém o pareamento esquerdo/direito.
- Aplicar pose e Undo restaura todos os transforms.

## 11. Fase 5 — Retarget Humanoid/Generic

### Escopo

- Humanoid: mapear por `HumanBodyBones` e Avatar válido.
- Generic: mapear por path, nome normalizado, hierarquia, lado, posição e orientação.
- `RigRetargetProfile` revisável com status por bone e overrides manuais.
- Preview source/destination lado a lado.
- Correção de rotação de referência, escala e root motion.
- Retarget de pose e bake de clip em taxa configurável.
- Métricas de posição/rotação para hands, feet, head e root.
- Batch de clips.
- MCP: `rig_analyze_retarget`, `rig_apply_retarget_pose`, `rig_retarget_clip`.

### Gate

- Nenhum bone não mapeado é ignorado silenciosamente.
- Profile hash invalida quando uma hierarquia muda.
- Bake reproduz samples do preview dentro das tolerâncias publicadas.

## 12. Fase 6 — Animation Rigging, IK e bake

### Escopo

- Solver Two Bone IK nativo para braço/perna.
- Targets/hints manipuláveis na Scene View.
- Aim/look-at e chain FABRIK nativos mínimos.
- Foot/hand placement presets.
- Bake do resultado para `.anim` standalone.
- Adapter reflexivo e opcional para `com.unity.animation.rigging`:
  - detectar pacote/tipos;
  - criar `Animator`, `RigBuilder`, `Rig`, `RigLayer` e a constraint equivalente: `TwoBoneIKConstraint`, `MultiAimConstraint` ou `ChainIKConstraint`;
  - configurar pela API pública refletida e validar `IsValid()` antes do commit;
  - degradar para solver nativo com mensagem clara.
- MCP: `rig_create_ik`, `rig_preview_ik`, `rig_bake_ik_clip`.

### Gate

- Preview não acumula drift entre frames.
- Bake e preview coincidem nos samples.
- Ausência do pacote opcional não causa erro de compilação.
- Presença de `com.unity.animation.rigging@1.4.1` cria e registra as três constraints equivalentes sem depender de campos privados serializados.

## 13. Fase 7 — Auto-rig e assistência por IA

### Escopo

- Analisar mesh estática e gerar proposta de landmarks/skeleton Humanoid.
- Heurística local obrigatória: bounds, simetria, slices, conectividade e extremidades.
- Geração de skeleton revisável antes do bind.
- Pesos iniciais por envelopes, distância geodésica aproximada e região anatômica.
- Preview de bones/heatmap/confiança.
- Correção manual de landmarks.
- Proposta IA opcional:
  - contrato JSON versionado;
  - imagens multi-view/mesh summary como entrada;
  - nunca enviar assets sem opt-in;
  - validar toda saída contra a mesh/revision/hash;
  - fallback local sempre disponível.
- MCP: `rig_analyze_auto_rig`, `rig_preview_auto_rig`, `rig_apply_auto_rig`.

### Gate

- Aplicação exige `proposalHash`, `meshHash` e confirmação.
- Baixa confiança bloqueia aplicação e aponta landmarks necessários.
- Resultado passa Rig Inspector e produz um clip de teste deformando todos os membros sem NaN.

## 14. UX da janela

```text
Rig & Animation Studio
├── Inspector
│   ├── personagem / renderer / clip set
│   ├── árvore do skeleton
│   └── relatório + heatmap
├── Skin Weights
├── Skeleton & Bind
├── Clips & Poses
├── Retarget
├── IK & Bake
└── Auto Rig
```

- A página ativa mantém seu label; páginas inativas podem usar ícone + tooltip quando o dock estiver estreito.
- Ações perigosas usam preview e confirmação explícita.
- Erros apontam bone, vertex, clip e caminho do asset quando aplicável.
- O Scene View mostra apenas o overlay da página ativa.

## 15. Testes e validação por fase

Fixtures mínimas:

- Humanoid válido;
- Humanoid com Avatar incompleto;
- Generic com nomes padronizados;
- Generic com nomes arbitrários;
- static humanoid simétrico;
- mesh com 1, 2, 4 e 8 influências;
- pesos inválidos/zero/out-of-range;
- bind poses divergentes;
- clips loop/root motion/extremos;
- personagem modular splitado;
- roupa skinned para transferência de pesos.

Por fase:

1. testes unitários dos serviços;
2. EditMode da UI/service/MCP;
3. compilação Unity 6000.3.5f2;
4. suíte completa sem novas falhas;
5. gate estático e release contract;
6. checklist visual no Editor quando houver Scene View/arraste.

## 16. Critério de conclusão global

- Sete páginas funcionais e separadas.
- Paridade UI/MCP para operações automatizáveis.
- Nenhuma mutação de importados.
- Undo/preview/validação comprovados.
- Rig Humanoid e Generic cobertos por fixtures.
- Weight painting, retarget e bake preservam canais.
- Auto-rig local funciona sem serviço externo.
- IA permanece opcional, auditável e fail-closed.
- Documentação de uso e limitações atualizada.

## 17. Registro de implementação — revisão de 2026-08-09

### Entregue

- Janela `Window > Asset Modeler > Rig & Animation Studio` com sete páginas independentes.
- Scene View para skeleton, heatmap, weight brush, handles de IK e preview de landmarks do Auto Rig.
- Serviços comuns usados pela UI e por 19 ferramentas MCP novas.
- Proteção de assets importados; pesos, bind poses, clips transformados, retarget, IK bake e Auto Rig escrevem cópias standalone sob `Assets/`.
- `BoneWeight1` preservado sem truncamento implícito em leitura, pintura, normalização, transferência e Auto Rig.
- Pose sampling protegido por snapshot/finally em validação, preview, retarget e IK bake.
- Retarget profile e Auto Rig proposal vinculados a hashes; mutações recusam revisão obsoleta.
- Integração opcional com `com.unity.animation.rigging` por reflexão. Quando o pacote está ausente, Two Bone, Aim e FABRIK nativos continuam disponíveis.
- Assistência por IA delimitada por contrato JSON `1.0`, opt-in, com `meshHash`, lista completa de landmarks e fallback local obrigatório.
- Hash de estado cobre hierarquia, meshes, bind poses e pesos; previews, poses, retarget e propostas obsoletos são recusados.
- Weight Painter agrupa cada arraste em um único Undo, permite cancelar com `Esc`, respeita bones/vértices/regiões bloqueados e usa caches de adjacência, espelho e heatmap. Picking usa mesh baked reutilizável, BVH de triângulos e grade espacial de vértices estáveis durante a pincelada; vértices intocados não são clonados/normalizados.
- Bind pose possui snapshot restaurável; rebuild mede desvio geométrico e reverte se ultrapassar a tolerância. Transferência usa BVH, limite de distância, região, mirror e preservação fora da região.
- Clip Studio entrega mirror Generic/Humanoid, additive quaternion real, merge multi-clip, remapeamento e root-motion extract/remove. Emendas de curvas numéricas e object-reference eliminam tempos duplicados antes de serializar.
- Retarget exige revisão por bone, invalida mudanças completas de estado, suporta batch e publica métricas de cabeça, mãos, pés, root e rotação.
- IK restaura a pose-base antes de cada preview, aceita chains FABRIK arbitrárias, presets de mãos/pés e bake da chain completa. O adaptador opcional usa a API pública refletida para Two Bone, Multi-Aim e Chain IK com Undo/rollback.
- Auto Rig bloqueia landmarks individualmente por confiança, gera pesos por envelopes anatômicos com difusão de vizinhança, tenta Avatar Humanoid, usa fallback Generic explícito e valida um clip de deformação antes do commit.
- O pedido para IA pode ser exportado pela UI ou devolvido por `rig_analyze_auto_rig`: inclui hash, resumo da mesh, baseline local, schema de resposta e referências multi-view por GUID/path, mas nunca incorpora nem envia os assets automaticamente.

### Evidência automatizada

- Unity `6000.3.5f2`, EditMode focado com `com.unity.animation.rigging@1.4.1`: `25/25` aprovados, incluindo Two Bone, Multi-Aim e Chain IK.
- Mesmo conjunto sem o pacote opcional: `22` aprovados, `3` ignorados por dependência ausente e `0` falhas.
- Unity `6000.3.5f2`, suíte completa com graphics device, uGUI `2.0.0` e Animation Rigging `1.4.1`: `275` total, `272` aprovados, `3` ignorados preexistentes e `0` falhas.
- Índice espacial determinístico: fixture com `8.192` triângulos testou apenas `8` triângulos no raycast; brush retornou `12` candidatos entre `4.225` vértices.
- Smoke visual isolado: skinned mesh com `340` vértices deformou por FABRIK até erro de ponta `0,001972`; `ChainIKConstraint.IsValid()` retornou verdadeiro e o PNG foi inspecionado com o mesh curvado em direção ao target.
- A mesma suíte em `-nographics` deixa `2` testes antigos de screenshot sem pixels renderizados; o assembly de modelagem permanece aprovado (`56` casos, `0` falhas). Isso é limitação comprovada do graphics device nulo, não regressão do rig.
- Compilação semântica Roslyn do módulo de rig contra as referências reais do Unity: `0` erros.
- Release contract: `com.maicon.scene-analyzer@0.18.2`, dependências pinadas, aprovado.
- Asset Modeler Phase 0 static gate: aprovado.

### Validação ainda dependente do Editor do usuário

- Legibilidade das sete páginas no layout/docking real.
- Densidade e oclusão do heatmap em personagens de produção.
- Sensação do brush, mirror e handles de IK com meshes densas.
- Retarget Humanoid com Avatars importados reais.
- Integração do Animation Rigging dentro do projeto principal, incluindo coexistência com os demais sistemas/Animators do jogo.
- Qualidade anatômica do Auto Rig em personagens não simétricos, T-pose/A-pose e roupas.

### Limites explícitos desta entrega

- O Auto Rig local cria skeleton semântico, skin, pesos iniciais e clip de validação. Ele tenta persistir um `Avatar` Humanoid por `AvatarBuilder`; se a anatomia proposta não satisfizer a validação da Unity, grava o status e opera como Generic em vez de declarar sucesso Humanoid incorretamente.
- O adaptador de IA exporta o pedido e valida/importa a resposta, mas não escolhe provedor nem envia dados por conta própria. Um transporte futuro deve exigir opt-in explícito e consumir exatamente os contratos versionados existentes.
- A aprovação automatizada não substitui o teste visual no projeto do usuário para oclusão do heatmap, ergonomia do brush/handles, Avatars de produção e qualidade anatômica do Auto Rig.
