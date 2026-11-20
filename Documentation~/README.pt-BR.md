# Unity MCP Transport (português)

Deixa agentes de IA (Claude Code, Codex ou qualquer cliente do [Model Context Protocol](https://modelcontextprotocol.io)) inspecionar e editar um projeto Unity pelo próprio Editor. Cobre cenas, prefabs, assets, pacotes, builds, testes, Play Mode e mais, com identidades estáveis, escritas com Undo, permissões por capacidade e salvamento só quando pedido.

O pacote (`com.maicon.scene-analyzer`) roda dentro do Editor. Um **broker** opcional em Python mantém o endereço MCP de pé enquanto a Unity compila, recarrega o domínio ou reinicia. Assim o agente não perde a conexão nem o trabalho.

A documentação completa, em inglês, está no [README](../README.md).

## Requisitos

- Unity 6000.1 a 6000.6, desenvolvido e testado na 6000.6.7f1. Windows é a plataforma principal.
- Python 3.10 ou mais novo, para o modo broker (o recomendado).

## Instalar

No Unity: **Window → Package Manager → + → Add package from git URL**:

```text
https://github.com/Dellacurtais/unity-coder.git
```

Acrescente `#vX.Y.Z` para fixar uma versão.

## Começar (modo broker)

1. Abra **Window → Unity MCP Transport** e escolha **Mode = Broker**.
2. Clique em **Install**. A Unity acha o Python, copia o broker para `%LOCALAPPDATA%\UnityMcp\Broker` e instala as dependências num ambiente próprio.
3. Clique em **Start**. Daí em diante, a Unity sobe o broker sozinha sempre que o Editor abre sem nenhum rodando.
4. Em **AI clients**, clique em **Install** ao lado do Claude Code ou do ChatGPT/Codex e reinicie o cliente.
5. Peça ao agente `get_server_health` e depois `self_test`.

## O que o broker garante

- **Supervisor por usuário**: sobrevive ao fechamento e à reabertura do Editor e reinicia o broker se ele sair ou parar de responder.
- **Chamadas longas**: depois de 45 s, a chamada responde `status: running` com um `requestId`. `broker_wait_request` devolve o resultado depois. Não repita a chamada.
- **Recarga de domínio**: leituras esperam a Unity voltar, em vez de falhar.
- **Token único**: a variável de usuário `UNITY_MCP_BROKER_TOKEN` vale para o Editor, o broker e os clientes.
- **Ciclo de vida do Editor**: `broker_close_editor` (só com cenas limpas), `broker_open_editor` e `broker_restart_editor`. `broker_editor_status` mostra os diálogos que travam a Unity.
- **Limpeza de órfãos**: fecha ajudantes da Unity cujo Editor morreu, decidindo pela árvore de processos. Nunca fecha um Editor nem o Unity Hub.

## Participar

Leia o [CONTRIBUTING](../.github/CONTRIBUTING.md) e o [Código de Conduta](../.github/CODE_OF_CONDUCT.md). Problemas de segurança vão em particular, como explica o [SECURITY](../.github/SECURITY.md). Issues e pull requests podem ser em português ou inglês.

## Licença

[MIT](../LICENSE.md).
