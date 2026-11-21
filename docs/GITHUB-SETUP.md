# Configuração manual do GitHub

Este documento lista o que precisa ser feito no GitHub e as etapas para sincronização e
release. A primeira sincronização já foi executada; o procedimento abaixo permanece
válido para configuração, recuperação e novos clones.

## 1. Repositório remoto

URL:

```text
https://github.com/Matehfields/Organessonium-desktop
```

O repositório remoto existe e contém a branch `main`. O repositório local usa a branch
`main` e o remoto `origin` já está configurado com tracking.

### Autenticação

O Git está configurado com o credential helper `manager` (Git Credential Manager). O
GitHub CLI (`gh`) não está instalado nesta máquina.

Em um novo clone ou no primeiro push de outra máquina, o Git Credential Manager pode
abrir uma janela de login do GitHub. Se isso não acontecer, use um Personal Access Token
(classic com escopo `repo` ou fine-grained com permissão de leitura/escrita em Contents)
como senha quando o Git solicitar. Nunca cole o token em um arquivo do repositório.

## 2. Sincronização

A primeira sincronização já foi executada. O workspace foi limpo antes do envio:
permanecem apenas o código, a mídia verificada e as builds de publicação em
`artifacts/releases`. `bin`, `obj`, caches, revisões e publicações intermediárias não são
versionados.

Para um novo clone, ou para repetir a configuração, execute com autorização:

```powershell
git status
git push -u origin main
```

Se `origin` não existir, configure antes:

```powershell
git remote add origin https://github.com/Matehfields/Organessonium-desktop.git
git remote -v
```

Se `origin` apontar para outro endereço, corrija:

```powershell
git remote set-url origin https://github.com/Matehfields/Organessonium-desktop.git
```

Depois do push:

1. Abra **Settings → General → Default branch** e confirme `main`.
2. Confira se o workflow `CI` iniciou no GitHub Actions.
3. Se a autenticação falhar, conclua o login pelo Git Credential Manager ou configure um
   token e tente novamente.

## 3. Proteção da branch main

Em **Settings → Branches → Add branch ruleset** (ou proteção de branch):

- exigir pull request antes do merge;
- exigir que a CI passe (`CI / Verificações e testes rápidos`);
- proibir force push e exclusão da branch;
- exigir resolução de conversas;
- permitir bypass apenas para o mantenedor, se necessário.

Isso mantém a `main` estável e permite que a release parta de um commit aprovado.

## 4. Permissões do GitHub Actions

Em **Settings → Actions → General**:

- permita GitHub Actions;
- configure o token padrão como **Read and write permissions**, pois o job de publicação
  precisa de `contents: write` para criar a release. Os workflows declaram permissões
  mínimas por job; a configuração do repositório é o limite máximo.
- desative **Allow GitHub Actions to create and approve pull requests**;
- para forks, exija aprovação de mantenedor na primeira execução.

Não adicione segredos globais desnecessários. A CI rápida não usa segredos.

## 5. Ambiente protegido de release

Em **Settings → Environments → New environment**, crie `release`:

- adicione pelo menos um revisor obrigatório (o mantenedor);
- limite deployment branches a `main`, pois o workflow é disparado a partir da `main`;
- adicione os secrets de assinatura somente se houver certificado:

  - `WINDOWS_SIGNING_PFX_BASE64`;
  - `WINDOWS_SIGNING_PFX_PASSWORD`.

- adicione a variable `WINDOWS_SIGNING_TIMESTAMP_URL` se o provedor exigir timestamp.

Sem certificado, a release funciona sem assinatura. Veja `docs/CODE-SIGNING.md`.

## 6. Release de mídia

Antes de qualquer compilação no GitHub Actions, publique os ativos descritos em
`docs/MEDIA-ASSETS.md` na release `media-assets-v1`. Sem eles, `build.yml` e `release.yml`
falham na verificação de fontes.

### 6.1 Build manual de portáteis e instaladores

Depois de publicar a release de mídia:

1. Abra **Actions → Full Build (manual) → Run workflow**.
2. Selecione a branch `main`.
3. Deixe `include_store_packages` desmarcado para compilar apenas os executáveis
   portáteis, os MSIs, as fontes e os metadados.
4. Execute e aguarde. O workflow baixa a mídia, compila, valida os artefatos e publica o
   artifact; os testes rápidos rodam depois do upload.
5. Baixe o artifact `organessonium-<versão>-build.<n>` ao final. Se os testes falharem,
   o job termina vermelho, mas o artifact já publicado continua disponível.

Os MSIX e o MSIXBundle são opcionais e aumentam bastante o tempo e o tamanho do artifact.
Para gerá-los no Actions, marque `include_store_packages`. Para compilá-los localmente,
use `release/Build-Releases.ps1` sem `-SkipStorePackages`.

## 7. Primeira release estável

Pré-requisitos:

- `Version.props` com a versão autorizada (`1.0.0` inicialmente);
- `CHANGELOG.md` com a seção `## [1.0.0] - AAAA-MM-DD` revisada, substituindo
  `## [Não lançado]`;
- CI verde na `main`;
- ambiente `release` configurado;
- ativos de mídia publicados;
- autorização explícita para push, tag e release.

Procedimento:

```powershell
# 1. Verificações locais
pwsh ./tools/check-repository.ps1
pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia

# 2. Commit e push (somente com autorização)
git add <arquivos>
git commit -m "build: prepara versão 1.0.0"
git push origin main

# 3. Tag anotada (somente com autorização)
git tag -a v1.0.0 -m "Organessônium v1.0.0"
git push origin v1.0.0
```

No GitHub:

1. Abra **Actions → Release → Run workflow**.
2. Selecione a branch `main`.
3. Informe `v1.0.0`.
4. Escolha se deseja incluir testes de interface e pacotes MSIX.
5. Execute e aprove o ambiente `release`.
6. Acompanhe a compilação e a publicação.
7. Confira notas, arquivos, tamanhos e `SHA256SUMS.txt` na release.

Se a execução falhar depois da criação do rascunho, execute novamente com a mesma tag.
O script retoma o rascunho. Não mova a tag nem sobrescreva uma release já publicada.

## 8. Privacidade e histórico

O histórico atual contém endereços de e-mail pessoais do mantenedor em commits antigos.
Um repositório público expõe esses endereços nos commits. Se isso for um problema, a
decisão de reescrever o histórico precisa ser tomada com autorização expressa. Não
reescreva o histórico por conta própria.

## 9. Licença

Nenhuma licença de uso ou redistribuição foi adicionada. O repositório pode ser público
para consulta, mas os direitos de uso, modificação e redistribuição dependem de decisão
explícita do mantenedor. Veja `CONTRIBUTING.md`.
