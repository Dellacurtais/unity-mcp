# Asset Modeler: limpeza e tamanho de modelos

Implementação no código-fonte do pacote, destinada ao Unity 6.3. A instalação em outros projetos e a atualização de um broker instalado são etapas diferentes da alteração deste checkout.

## Fluxo da janela

Abra **Window → Asset Modeler (Unity MCP)**. Escolha a **raiz do modelo** explicitamente. Em modelos com várias malhas, escolha também a malha que será limpa; o ajuste de tamanho usa a raiz inteira.

1. **Limpar geometria**: crie uma cópia editável, analise a geometria, revise as opções e gere a prévia. Aplique somente após revisar as contagens e as partes que serão removidas. Outra malha do mesmo modelo pode ser preparada dentro da cópia existente.
2. **Ajustar tamanho**: escolha manter o tamanho atual ou informar uma dimensão em X, Y ou Z. Gere a prévia e confira as medidas. A aplicação cria uma nova hierarquia e assets independentes, com escala local da raiz `1,1,1`, e salva um prefab.
3. **Salvar cópia**: salva o resultado editável como prefab. Os detalhes do documento e as ferramentas de construção, topologia, deformação e ajuste de roupa ficam separados do fluxo principal.

A janela não oferece mais controles próprios de mover/girar objetos, reposicionar pivô, alinhar ou distribuir. Use as ferramentas nativas do Unity. Seleção e edição de elementos da malha continuam disponíveis nas ferramentas avançadas.

## O que a limpeza remove

Por padrão, remove vértices sem referências e triângulos exatamente degenerados. A área mínima começa em **zero** e é medida em unidades locais ao quadrado. Um valor positivo é uma escolha explícita que pode apagar superfícies finas válidas.

Linhas e pontos soltos são opcionais. Uma linha é solta quando não coincide, por índices, com uma aresta de uma face restante; um ponto é solto quando não pertence a uma face restante. Remover um trecho de LineStrip não cria uma ligação artificial entre os trechos restantes.

Fragmentos com faces válidas nunca são excluídos automaticamente pelo tamanho. A conectividade usa arestas com índices compartilhados, sem solda por posição: olhos, dentes, acessórios e costuras de UV podem formar componentes separados. A lista permite selecionar partes, focá-las na Scene View e conferir a prévia.

As posições dos vértices sobreviventes, canais, pesos, bindposes e frames de blendshape são preservados na limpeza. O snapshot copia os buffers brutos, incluindo formatos, streams e dimensões UV, e conserva `baseVertex` no roundtrip. Combinações que o Unity não consegue restaurar sem mudar o layout são recusadas antes de alterar o asset. Soldar continua sendo uma operação distinta.

## Escala de modelos estáticos e com rig

**Manter tamanho atual** incorpora a escala uniforme positiva da raiz. Exemplo: mesh com altura 100 e escala `0,018` resulta em altura 1,80 e escala `1`.

**Definir dimensão** calcula um fator uniforme a partir da geometria de repouso para preservar proporções. As dimensões seguem os eixos locais da raiz e não incluem a escala herdada de um pai. Portanto, raiz local `1` não significa escala mundial `1` quando um ancestral tem outra escala.

A conversão considera a hierarquia completa: posições descendentes, meshes, deltas de posição dos blendshapes, bindposes, bounds, colliders suportados e dependências de animação. Avatares Humanoid são reconstruídos em coordenadas de repouso, separadas da posição e rotação da instância. Clips e overrides são copiados; curvas de translação genéricas são convertidas, e a escala física do novo Avatar trata a translação normalizada do Humanoid. Os dados originais permanecem intactos. Referências internas são remapeadas para a cópia. Campos numéricos próprios de scripts, como alcance de ataque e distâncias de gameplay, não são redimensionados automaticamente.

O preflight recusa casos sem uma política segura: raiz espelhada ou com escala não uniforme, meshes sem Read/Write, ossos externos ou rig otimizado sem hierarquia exposta, Animators aninhados, componentes físicos/constraints não suportados e determinadas curvas ou trocas animadas de assets. Root motion genérico com Avatar exige o motion node do ModelImporter disponível. Materiais e controllers-base precisam estar salvos como assets para não gerar um prefab com referências transitórias. A mensagem identifica o motivo; nenhum resultado parcialmente convertido deve ser salvo. Os limites e os testes executados nesta alteração estão registrados abaixo.

**Undo/Redo da criação** controla a cópia na cena. Arquivos gerados permanecem disponíveis para não invalidar prefabs que já foram salvos. As operações de limpeza na mesh editável possuem o histórico/Undo existente.

## Uso por agentes

- `mesh_analyze_cleanup`: diagnóstico paginado (até 200 componentes por página), hash da mesh e revisões. Use `expectedMeshHash` nas páginas seguintes para rejeitar resultados misturados após uma mudança.
- `mesh_preview_cleanup` / `mesh_apply_cleanup`: parâmetros tipados de limpeza, revisões esperadas e `previewHash` obrigatório na aplicação. Ao selecionar fragmentos por `removeComponentIds`, envie também `expectedMeshHash` do diagnóstico para impedir que uma alteração externa redirecione a seleção para outra parte.
- `model_preview_scale` / `model_apply_scale`: raiz explícita, modo `apply_current_scale` ou `target_dimension`, eixo, dimensão e pasta. A aplicação exige `expectedSourceHash` e `previewHash` e devolve a nova raiz e os caminhos criados.

Os identificadores das ferramentas de modelagem anteriores continuam disponíveis para compatibilidade. A remoção dos controles duplicados refere-se à interface da janela. Novos fingerprints incluem o layout e os bounds: preserve resultados de sessões antigas antes de criar uma nova sessão se a versão anterior acusar mudança de fonte.

## Validação

Os testes usam projetos descartáveis no Unity **6000.3.5f2**, separados dos projetos de jogo. Incluem preservação dos buffers de mesh, fragmentos e revisões, cópia independente de rigs e colliders, escala estática sob pais transformados, poses e blendshapes conferidos por skinning CPU independente, Humanoid/root motion nos dois modos, overrides e persistência de clips legacy após reimportar o prefab.

A janela é exercitada por testes de Layout/Repaint em larguras de 390 e 650 pixels, com modelos de uma ou várias malhas e com rig. Isso verifica funcionamento e exceções de IMGUI; não substitui avaliação visual e de usabilidade manual. Casos recusados pelo preflight não são declarados compatíveis.

Resultados locais em 2026-09-23:

- Suíte completa `Maicon.SceneAnalyzer`, perfil representativo: **410 aprovados, 0 falhas, 4 ignorados**, de 414 testes. Três ignorados exigem o pacote opcional Animation Rigging; um exige Addressables.
- Escala no perfil mínimo: **25/25 aprovados**, sem testes ignorados, antes da proteção adicional de seleção na ferramenta de limpeza MCP.
- Broker: **65/65 aprovados**, incluindo o catálogo `modeling` com as ferramentas `model_*`.
- Importação/registro final: **372 verificações aprovadas**, 340 ferramentas registradas e zero erros de Console no projeto representativo.
- HTTP → broker privado → Unity: **18/18 verificações aprovadas**, com descoberta pelo perfil de modelagem, proteção de seleção/revisões/hashes, limpeza e escala até altura 1,80, prefab salvo e releitura independente de dimensões/escala. Os objetos temporários foram removidos sem falhas.
- Roslyn: 432 arquivos C#, zero erros sintáticos. Gates estáticos de release e modelagem aprovados.

Evidências preservadas: `.validation/unity63-ci/representative-m4/artifacts/full-editmode.xml`, `full-editmode.log`, `import-report.json` e `package-source-sha256.json` (1.015 arquivos; hashes conferidos contra o checkout); escala em `.validation/unity63-ci/minimal-sr/artifacts/scale-r6.xml`; broker em `.validation/modeler-broker-final.txt`. O último projeto reutilizou apenas o cache Library da execução anterior, mantendo fonte e manifesto separados; a nova regressão do processador foi descoberta e executada. As execuções anteriores, inclusive as que revelaram problemas corrigidos, permanecem em diretórios distintos.

O relatório integrado está em `.validation/modeler-live-m4/modeler-smoke.json`. A primeira execução revelou que o enriquecimento global do processor adicionava outro campo `target` dentro de referências já completas, impedindo seu reaproveitamento no schema estrito; o processor foi corrigido e recebeu regressão. O relatório anterior foi preservado em `.validation/modeler-live-final/modeler-smoke-before-target-fix.json`. O smoke final usa a referência integral retornada, sem retirar campos ou afrouxar o schema.

Não foi executada uma etapa de instalação em projetos de jogo ou no broker gerenciado existente; a ativação da nova versão nesses ambientes não faz parte desta evidência.

### Correção de renderizadores sem MeshFilter após 0.19.0

O seletor da janela tentava ler `sharedMesh` de renderizadores de efeitos sem `MeshFilter`, como `Wolf_Beam_Halo`. A exceção interrompia o grupo de layout e produzia também `Invalid GUILayout state`. O acesso agora usa nulidade da Unity, aceita somente MeshRenderer/SkinnedMeshRenderer com malha e ignora os demais no seletor. Os grupos de contexto e rolagem usam scopes. Remover o filtro selecionado invalida a prévia imediatamente e limpa a sessão da janela no próximo desenho, sem adicionar componentes ao objeto. As consultas equivalentes de escala, Surface Fit e Smart Body Split usam a mesma proteção; a normalização continua recusando efeitos sem política de conversão.

Validação específica no Unity 6000.3.5f2: **133 testes de modelagem aprovados, zero falhas e 3 ignorados** por ausência do pacote opcional Animation Rigging, de 136 testes. Os 10 casos novos incluem Layout/Repaint real com meshes, efeitos e filtro removido, referências destruídas e rejeição de escala antes de criar assets. Evidência: `.validation/unity63-ci/representative-g1/artifacts/modeling.xml`, `modeling.log` e `package-source-sha256.json`. Roslyn: 434 arquivos, zero erros; gates estáticos de modelagem/release aprovados. Esta rodada é de modelagem, não uma nova execução da suíte completa de 414 testes registrada acima.

O projeto de jogo observado nesta correção usa o pacote Git 0.19.0, commit `b10d9424b910160fb8d1db856699ff6a7197638f`. A correção permanece no checkout; não houve publicação, alteração do PackageCache ou mudança no projeto de jogo.
