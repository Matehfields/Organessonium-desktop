Você deve seguir exatamente o escopo solicitado pelo usuário. Não altere, remova ou substitua comportamentos, funções ou features sem autorização explícita.
Para bugs específicos, limite o diagnóstico a poucas etapas objetivas. Após encontrar uma hipótese plausível e evidência concreta no código, implemente uma correção mínima. Se a correção falhar, explique a causa e pare antes de abrir novas frentes de investigação.
Não usar travessão: —
Uma funcionalidade só é considerada concluída quando seu efeito é visível na interface correspondente. Corrigir apenas a lógica interna não caracteriza conclusão se a UI continuar sem exibir o resultado esperado.
Não crie dezenas de testes unitários para alterações pequenas de interface.
Você está proibido de usar a palavra "próprio"
Toda vez que algo for mexido, um bug corrijido, uma nova função implementada, faça um teste de regressão, garantindo que nada foi quebrado e que o problema foi corrijido.
Prioridade:

1. Corrigir o problema.
2. Compilar.
3. Verificar que não existem erros de compilação.
4. Fazer apenas os testes realmente relacionados à alteração.

Exemplos:

- Alterou o scroll:
  testar somente scroll.

- Alterou upload:
  testar upload.

- Alterou downloads:
  testar downloads.

- Alterou thumbnails:
  testar thumbnails.

Não criar dezenas de testes artificiais apenas para aumentar cobertura.

Se forem criados testes automatizados, eles devem validar o comportamento real do recurso modificado, e não apenas chamadas de funções internas.

Prefiro poucos testes relevantes a centenas de testes superficiais.
Não refatore arquivos, módulos ou fluxos não relacionados ao bug atual. A correção deve ter o menor diff possível.

Para bugs específicos:

máximo de 20 minutos de diagnóstico;
após localizar uma hipótese plausível, implementar a correção;
se a primeira correção falhar, apresentar o motivo e pedir orientação em vez de continuar investigando indefinidamente;
evitar repetir análises já feitas;
registrar apenas descobertas novas.

Não repetir frases como "vou seguir o fluxo completo", "vou confirmar", "vou confrontar", "vou rastrear" sem produzir uma alteração concreta no código. Cada nova etapa de investigação deve resultar em uma descoberta objetiva ou em uma modificação efetiva.
Antes de editar, informe em no máximo 5 linhas:
- causa provável;
- arquivos que serão alterados;
- mudança mínima planejada.
Depois execute sem repetir o diagnóstico.
DOCUMENTAÇÃO DO PROJETO

Mantenha um README.md na raiz do projeto como documentação técnica viva do Organessônium.

O README.md deve documentar o funcionamento REAL e ATUAL do aplicativo com profundidade suficiente para que uma nova sessão/agente consiga entender a arquitetura e os principais comportamentos sem precisar redescobrir o projeto inteiro.

Antes de iniciar uma tarefa relevante:
1. Leia o README.md.
2. Consulte os arquivos diretamente relacionados à tarefa.
3. Não trate o README.md como autoridade absoluta se o código atual contradizê-lo. O código é a fonte final de verdade.
4. Se encontrar documentação desatualizada, corrija-a.

O README.md deve documentar, quando aplicável:

- objetivo e funcionamento geral do Organessônium;
- arquitetura do projeto;
- estrutura dos principais diretórios;
- responsabilidades dos principais módulos, services, repositories, models, ViewModels/controllers e telas;
- fluxo de inicialização;
- sistema de configurações e persistência;
- comunicação com Discord;
- estrutura utilizada no servidor;
- canais/fóruns utilizados e responsabilidade de cada um;
- formato e significado dos metadados;
- estrutura de arquivos e pastas;
- relações entre fileId, folderId, parentFolderId, threadId, messageId, deviceId e outros identificadores importantes;
- upload e divisão em chunks;
- download e reconstrução;
- previews e thumbnails;
- previews de vídeo;
- sincronização de fotos e vídeos;
- identificação e visualização de dispositivos sincronizados;
- sistema de transferências e retomada;
- cache, quando existir;
- navegação;
- comportamentos importantes da interface;
- limitações técnicas conhecidas;
- decisões arquiteturais importantes e o motivo delas;
- diferenças relevantes entre plataformas, quando aplicável.

Não transforme o README.md em um histórico de cada pequena alteração.

Documente o ESTADO ATUAL do sistema.

Se uma implementação antiga foi substituída, atualize a documentação correspondente em vez de acumular descrições obsoletas.

Não coloque no README:
- tokens;
- senhas;
- credenciais;
- IDs privados desnecessários;
- dados pessoais;
- logs gigantes;
- detalhes triviais de implementação que podem mudar constantemente.

Após implementar uma mudança que altere arquitetura, comportamento, formato de dados, fluxo ou feature documentada, atualize o README.md na mesma tarefa.

Correções pequenas que não alteram o comportamento documentado não precisam modificar o README.

==================================================
VERSIONAMENTO E SEGURANÇA COM GIT
==================================================

Use Git obrigatoriamente como mecanismo de proteção contra regressões.

ANTES de modificar código:

1. Execute `git status`.
2. Verifique o `HEAD` atual.
3. Certifique-se de que existe um estado seguro ao qual seja possível retornar.
4. Se o worktree estiver limpo, o HEAD atual já pode ser usado como ponto seguro. NÃO crie commits de backup redundantes.
5. Informe de forma curta:

SAFE ROLLBACK: <hash curto>

Se existirem alterações não commitadas antes da tarefa:
- não descarte;
- não sobrescreva;
- descubra de onde vieram;
- preserve trabalho legítimo;
- só prossiga depois de entender o estado.

Nunca use `git reset --hard`, `git clean`, checkout destrutivo ou operação equivalente sobre trabalho legítimo sem autorização explícita do usuário.

==================================================
DURANTE A ALTERAÇÃO
==================================================

Mantenha o escopo restrito ao que foi solicitado.

Não aproveite uma tarefa para:
- refatorar código não relacionado;
- reorganizar o projeto;
- remover funcionalidades;
- mudar comportamentos;
- alterar formatos;
- fazer "melhorias" não solicitadas.

Prefira o menor diff capaz de resolver corretamente o problema.

Não remova nenhuma função, feature ou comportamento existente sem autorização explícita do usuário.

==================================================
DEPOIS DA ALTERAÇÃO
==================================================

Antes de considerar a tarefa concluída:

1. Execute `git diff`.
2. Verifique quais arquivos foram alterados.
3. Confirme que não existem mudanças acidentais.
4. Compile/verifique o projeto.
5. Execute somente os testes relevantes para a alteração.
6. Quando possível, valide o comportamento real, principalmente para bugs de interface.
7. Atualize o README.md somente se a alteração afetar algo que precisa ser documentado.
8. Se o resultado estiver correto, faça um commit específico.

Use mensagens de commit descritivas, por exemplo:

`Fix synchronized devices visualization`
`Preserve devices state between tabs`
`Add proxy configuration`
`Improve parallel preview loading`

Evite mensagens vagas como:

`fix`
`changes`
`updates`
`various improvements`

Cada commit deve representar preferencialmente uma mudança lógica.

==================================================
SE UMA ALTERAÇÃO DER ERRADO
==================================================

Não empilhe remendos indefinidamente.

Compare a implementação problemática com o SAFE ROLLBACK usando Git.

Identifique exatamente o que a alteração introduziu.

Se a tentativa estiver fundamentalmente errada, prefira retornar ao último estado funcional e tentar novamente com uma abordagem limpa em vez de continuar construindo em cima de uma regressão.

Nunca declare sucesso apenas porque:
- compilou;
- testes passaram;
- não existem erros estáticos.

Para bugs funcionais, sucesso significa que o comportamento solicitado realmente funciona.

==================================================
README COMO CONTEXTO ENTRE SESSÕES
==================================================

O README.md deve funcionar como memória técnica persistente do projeto.

Uma nova sessão deve conseguir lê-lo e entender rapidamente:
- o que é o Organessônium;
- como os principais sistemas funcionam;
- quais componentes são responsáveis por cada comportamento;
- quais contratos de dados precisam ser preservados;
- quais decisões importantes não devem ser quebradas.

Entretanto, não deixe de investigar o código relevante antes de alterar algo. O README fornece contexto; ele não substitui a leitura do código.

## Higiene do workspace

- Mantenha o repositório enxuto. Builds de teste, revisões visuais, publicações intermediárias, caches e saídas de depuração não são código-fonte e não devem se acumular.
- Preserve `artifacts/releases` como único destino local de builds de publicação. `artifacts/release-work`, `artifacts/media-build` e pastas de revisão são descartáveis.
- Ao concluir uma rodada, remova `bin`, `obj`, `TestResults`, `.vs`, caches locais e artefatos de revisão que não sejam necessários para a próxima publicação.
- Não crie dezenas de arquivos de teste para tarefas pequenas. Valide o comportamento real com poucos testes relevantes e remova testes descartáveis que não protejam uma regressão real.
- Nunca versione `bin`, `obj`, `artifacts`, `releases`, mídia verificada, bancos, logs ou pacotes.

# Instruções para agentes de programação

Este arquivo orienta Codex, OpenCode e outros agentes que trabalhem no Organessônium
Desktop. Leia também o `git.md`, que detalha versionamento, testes, compilação e
publicação.

## Regras obrigatórias

1. **Não faça push, force push, crie tags remotas ou publique releases sem autorização
   explícita do mantenedor.**
2. **Não execute compilação completa de distribuição sem autorização.** Verificações
   leves, compilação Debug/Release e testes rápidos são permitidos.
3. **Não altere a versão pública em `Version.props` sem autorização.** A escolha entre
   PATCH, MINOR e MAJOR também depende de aprovação.
4. **Incremente a build exatamente uma vez por rodada concluída** de desenvolvimento ou
   correções, antes do commit correspondente. Não incremente por arquivo, por execução
   de teste ou por workflow do GitHub Actions.
5. **Não reescreva o histórico Git** (`rebase`, `filter-branch`, `push --force`, amend de
   commits já publicados) sem autorização.
6. **Não inclua credenciais, tokens, certificados, chaves privadas, dados pessoais ou
   caminhos absolutos da máquina** no repositório, em logs, artefatos ou mensagens.
7. **Preserve alterações não commitadas do usuário.** Não descarte, sobrescreva ou
   commite trabalho alheio sem revisar.
8. **Não remova arquivos ou configurações existentes** sem investigar a finalidade.
9. **Não introduza dependências, refatorações ou mudanças funcionais** fora do escopo.
10. **Não afirme que algo foi validado no GitHub Actions** se o workflow ainda não foi
    executado em um ambiente real.

## Fluxo de uma rodada

1. Leia os arquivos relevantes e verifique o estado do Git.
2. Implemente a menor mudança consistente com a arquitetura existente.
3. Execute as verificações leves e os testes rápidos:

   ```powershell
   pwsh ./tools/check-repository.ps1
   pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia
   ```

4. Se a mudança afetar a versão ou a infraestrutura, valide também os scripts e
   arquivos de configuração.
5. Antes do commit, incremente a build uma vez:

   ```powershell
   pwsh ./tools/version.ps1 bump-build -ExpectedBuild <build-lida-no-início>
   ```

   Se outro agente já tiver incrementado a build, o comando falha. Revise antes de
   tentar novamente.
6. Atualize o `CHANGELOG.md` quando a mudança for relevante.
7. Se o usuário autorizou commit, crie um commit pequeno e descritivo em Conventional
   Commits. Não faça push sem autorização.

## Versionamento

- `Version.props` é a única fonte da versão pública e da build.
- Não edite números de versão em `.csproj`, scripts, manifests MSIX ou configurações de
  release.
- Use `pwsh ./tools/version.ps1 show` para consultar.
- Use `pwsh ./tools/version.ps1 set-version -Version 1.0.1 -Authorized` somente após
  autorização explícita. O comando também incrementa a build.
- Formatos derivados: `AssemblyVersion A.B.C.0`, `FileVersion A.B.C.build`,
  `InformationalVersion A.B.C+build.N`, pacotes Windows `A.B.C.0`.

## Testes e compilação

- Suíte rápida: `pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia`.
- Testes de interface: adicione `-IncludeUi` (podem abrir janelas WebView2).
- Testes de mídia exigem `runtime-package.zip` e, para alguns casos, `ffprobe.exe`.
- Compilação local:

  ```powershell
  dotnet restore Organessonium/Organessonium.slnx
  dotnet build Organessonium/Organessonium.slnx -c Release
  dotnet run --project Organessonium/Organessonium/Organessonium.csproj
  ```

- Compilação completa de distribuição: `./release/Build-Releases.ps1`, somente com
  autorização. Ela exige os ativos de mídia verificados e o Windows SDK.

## Publicação

- O workflow `release.yml` só pode ser acionado manualmente a partir da `main`, com uma
  tag anotada existente e uma seção revisada no `CHANGELOG.md`.
- A publicação usa rascunho e ambiente protegido. Nunca sobrescreva uma release já
  publicada; para corrigir, publique uma nova versão PATCH.
- Consulte o procedimento completo em `git.md` e `docs/GITHUB-SETUP.md`.

## Licença

O projeto não possui licença de uso/redistribuição definida. Não adicione uma licença
open source, altere textos de licença ou conceda direitos em nome do mantenedor sem
autorização explícita.
