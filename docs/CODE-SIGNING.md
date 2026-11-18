# Assinatura digital para Windows

## Situação atual

O projeto compila e distribui artefatos sem assinatura quando nenhum certificado está
configurado. A assinatura é opcional e nunca deve ser simulada com certificados
inventados.

O fluxo já assina, quando `ORGANESSONIUM_PFX_PATH` é informado:

- executáveis portáteis (`Organessonium-x86.exe`, `x64`, `arm64`);
- MSIs (`Organessonium-installer-*.msi`);
- MSIX individuais (`msix/Organessonium_*.msix`);
- MSIXBundle.

O `SignTool` oficial do Windows SDK é usado com SHA-256. O certificado e a senha não
entram no repositório, nos artefatos ou nos logs.

## Variáveis e secrets

Compilação local:

| Variável | Obrigatória | Descrição |
|---|---|---|
| `ORGANESSONIUM_PFX_PATH` | para assinar | caminho local do arquivo PFX |
| `ORGANESSONIUM_PFX_PASSWORD` | opcional | senha do PFX, somente no ambiente |
| `ORGANESSONIUM_TIMESTAMP_URL` | opcional | URL RFC 3161 de timestamp |

GitHub Actions, ambiente protegido `release`:

| Nome | Tipo | Conteúdo |
|---|---|---|
| `WINDOWS_SIGNING_PFX_BASE64` | secret | PFX codificado em Base64 |
| `WINDOWS_SIGNING_PFX_PASSWORD` | secret | senha do PFX |
| `WINDOWS_SIGNING_TIMESTAMP_URL` | variable | URL de timestamp, por exemplo a do provedor do certificado |

O workflow grava o PFX apenas em `%RUNNER_TEMP%`, usa durante a build e remove ao final.
Nunca faça commit do PFX, da senha ou de um arquivo `.env` com esses valores.

Exemplo de codificação local do PFX para o secret:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('assinatura.pfx')) |
  Set-Clipboard
```

Cole o valor diretamente no secret do GitHub. Não salve o Base64 em um arquivo
versionado.

## Como obter um certificado

Não compre nem configure serviços pagos sem decisão do mantenedor. Opções comuns:

- certificado de assinatura de código OV ou EV de uma autoridade certificadora;
- Azure Trusted Signing, com identidade e política configuradas no Azure;
- certificado interno para testes, nunca distribuído ao público.

Um certificado autoassinado serve para testes locais, mas não é confiável em outras
máquinas e não deve ser apresentado como assinatura de distribuição.

## Integração futura com SignTool

O script `release/Build-Releases.ps1` monta os argumentos:

```text
sign /fd SHA256 /f <pfx> [/p <senha>] [/tr <timestamp-url> /td SHA256] <arquivo>
```

Se o certificado exigir um provedor de assinatura (por exemplo, Azure Trusted Signing),
será necessário adaptar o script para chamar a ferramenta oficial correspondente. Não
implemente essa integração sem o serviço e as credenciais aprovados.

## Verificação

Em uma máquina Windows com o Windows SDK:

```powershell
Get-AuthenticodeSignature .\Organessonium-x64.exe | Format-List Status,StatusMessage,SignerCertificate
```

Resultados esperados:

- `Status = Valid` para uma assinatura confiável e com timestamp;
- `Status = NotSigned` quando a assinatura não foi configurada;
- `UnknownError` ou `NotTrusted` para certificado autoassinado ou cadeia incompleta.

A assinatura não garante reputação imediata no SmartScreen; ela é construída com
histórico de downloads e tempo. Nunca prometa que a assinatura elimina avisos do
SmartScreen.

## Rotação e revogação

- Mantenha o certificado fora do repositório e com backup seguro.
- Se o PFX vazar, revogue o certificado junto à autoridade certificadora e remova os
  secrets do GitHub.
- Atualize a senha e o Base64 do secret após a rotação.
- Não reescreva releases antigas para trocar assinaturas; publique uma nova versão.
