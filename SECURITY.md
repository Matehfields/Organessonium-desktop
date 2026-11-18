# Segurança

## Versões suportadas

As versões estáveis publicadas na página de releases do GitHub recebem correções de
segurança conforme a gravidade e a disponibilidade do mantenedor. Versões antigas podem
não receber correções.

## Como relatar uma vulnerabilidade

Não abra uma issue pública com detalhes exploráveis, credenciais, tokens, dados de
usuários ou caminhos locais.

Prefira o recurso **Security → Advisories → Report a vulnerability** do repositório no
GitHub. Se ele não estiver disponível, entre em contato com o mantenedor pelo perfil
`Matehfields` no GitHub, sem incluir segredos na primeira mensagem.

Inclua:

- versão afetada;
- sistema operacional;
- passos de reprodução;
- impacto observado;
- se possível, uma sugestão de correção.

Não teste a vulnerabilidade em dados reais de terceiros, servidores do Discord ou
instalações de outras pessoas.

## Segredos e dados

O repositório não deve conter:

- tokens do Discord, GitHub ou outros serviços;
- senhas;
- chaves privadas ou certificados de assinatura;
- bancos de dados de usuários;
- caminhos absolutos da máquina;
- dados pessoais desnecessários.

Se uma credencial for exposta, revogue-a imediatamente. Adicionar o arquivo ao
`.gitignore` não remove o segredo de commits anteriores; o histórico precisaria ser
tratado com autorização e coordenação.

## Assinatura digital

A infraestrutura permite assinar executáveis, MSIs, MSIX e o MSIXBundle quando um
certificado é configurado. Nenhum certificado ou senha é armazenado no repositório.
Enquanto não houver certificado, as builds permanecem funcionais e sem assinatura.
Consulte `docs/CODE-SIGNING.md`.

## Dependências

As dependências vêm do feed oficial do NuGet e são fixadas nos `.csproj`. O Dependabot
propõe atualizações semanalmente; toda atualização passa pela CI e por revisão. Não
adicione feeds ou pacotes não verificados sem justificativa.

## CI/CD

- Workflows usam permissões mínimas.
- Pull requests de forks não recebem segredos.
- `pull_request_target` não é utilizado.
- O workflow de release exige aprovação do ambiente protegido `release`.
- Artefatos de build são retidos por tempo limitado e não publicam releases
  automaticamente.
