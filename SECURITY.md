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

## Proteções no aplicativo

- O token do bot e a senha do proxy são protegidos com DPAPI no escopo do usuário atual.
  Valores antigos em texto simples são migrados na leitura e as chaves legadas são
  removidas com a limpeza segura do SQLite. Se o material protegido estiver indisponível,
  salvar outras preferências mantém esse material para recuperação. A interface recebe
  apenas indicadores de configuração, nunca os valores.
- Um `config.ini` legado usado para importar configurações não é removido nem alterado;
  ele ainda pode conter um token em texto simples. Uma cópia fora da pasta de dados atual
  também pode ser importada novamente após o logout. Verifique e trate esses arquivos
  legados antes de compartilhar o computador, preservando os dados necessários a instalações antigas.
  A limpeza segura do SQLite não elimina cópias anteriores, backups ou dados remanescentes
  em armazenamento fora do banco ativo.
- O token é enviado apenas às requisições destinadas à API do Discord. Downloads de
  anexos não recebem esse cabeçalho de autenticação.
- Quando o coletor de diagnóstico está indisponível, o log alternativo registra tipos,
  códigos e pontos da pilha de exceções, sem mensagens ou detalhes que possam conter segredos.
- A chave de criptografia personalizada é exportada no formato `OEK2`, protegido por senha
  do usuário com PBKDF2-SHA256 e AES-GCM. Arquivos `OEK1` de versões anteriores continuam
  importáveis como formato legado. A senha não é armazenada. O Android verificado ainda
  aceita `OEK1`, mas não importa `OEK2`; a compatibilidade do novo formato depende de uma
  atualização no Android. O formato antigo não oferece confidencialidade por senha.
- Mensagens enviadas pelo WebView2 são aceitas apenas de `https://organessonium.local/index.html`
  e `setup.html`, com allowlist de comandos, limite de tamanho e validação dos argumentos.
  Navegação para outros endereços é bloqueada; links externos legítimos são abertos fora do
  WebView2, no navegador do sistema.
- As páginas usam CSP restritiva. Markdown e o editor permitem apenas links `http`, `https`
  e `mailto`, imagens `http`, `https` e os esquemas internos de prévia, e removem scripts,
  eventos e estilos não permitidos. A leitura de PDFs usa o host virtual de mídia e o
  worker local do PDF.js.
- Sair da conta remove as credenciais do banco e os dados locais associados ao login,
  sujeito à ressalva sobre arquivos legados externos acima.

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
