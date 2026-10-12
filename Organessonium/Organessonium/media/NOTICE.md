# Motor de mídia FFmpeg

O Organessônium executa `media/ffmpeg.exe` como programa separado, por argumentos e pipes de mídia. Não vincula libav ao código C#. O componente é substituível; não altere seu nome nem omita a licença na distribuição.

Build usada: `N-126168-gb16b5f2a01-20260815`, BtbN, Windows x64, aproximadamente 139 MiB, SHA-256 `4762609c60e0c6acfa478b6421d06fd1d1ebba4f5f033aac51ea316d960b8d1d`.

Esta build habilita `--enable-gpl --enable-version3`, incluindo libx264. A licença do executável é GPL versão 3 ou posterior, não apenas LGPL. O texto está em `GPL-3.0.txt`. As licenças e fontes das dependências também devem acompanhar o pacote de fontes correspondente.

As receitas BtbN são distribuídas com o aviso MIT original em `BtbN-LICENSE.txt`. Esses avisos acompanham o executável de mídia e o ZIP de código-fonte C#; não atribuem uma nova licença ao restante do Organessônium.

- Projeto e orientações: <https://ffmpeg.org/legal.html>
- Fonte FFmpeg: <https://github.com/FFmpeg/FFmpeg/tree/b16b5f2a01>
- Receitas do fornecedor: <https://github.com/BtbN/FFmpeg-Builds>

`source-manifest.json` identifica as fontes/receitas e arquivos reunidos para esta build. O workflow do fornecedor [31883715790](https://github.com/BtbN/FFmpeg-Builds/actions/runs/31883715790) confirma a revisão de receitas `590a6612d7d961e9258429e501619e0b7d7cbedf`. Entretanto, `scripts.d/50-rav1e.sh` executa `cargo update cc`; o snapshot das receitas não determina sozinho a revisão transitiva usada naquele dia. Os logs desse build precisam ser consultados e as fontes de todas as dependências precisam ser reunidas, incluindo fontes adicionais/submodules e alterações das receitas. Não foi declarada conformidade completa dessa build.

O pacote de fontes deve ser distribuído junto dos downloads públicos do app, como artefato separado, sem obrigar a instalação de fontes no computador do usuário. Manter somente um link para o projeto ou este aviso não substitui a entrega do Corresponding Source exigido pela GPL. Verifique a correspondência de TODAS as dependências antes de uma distribuição pública, assim como as condições de patentes aplicáveis. Este aviso não é uma certificação jurídica.

`Verify-Sources.ps1` confere a identidade do binário e a presença das licenças/avisos antes de publicar. Enquanto `redistributionReady` for `false`, informa a pendência, mas permite o build e a publicação local solicitados. O gerador inclui `media` nos instaladores/pacotes, os avisos no código-fonte C# e `ffmpeg-source-reference.zip` com os snapshots FFmpeg/BtbN identificados e conferidos por SHA-256. Esse ZIP de referência NÃO é apresentado como Corresponding Source completo e não encerra as obrigações de distribuição pública.

Depois de reunir e conferir todas as fontes/dependências, registre em `correspondingSourceArchive` o caminho relativo a `media` e SHA-256 do pacote completo, e somente então altere `redistributionReady`. Nesse estado, a verificação exige o arquivo correspondente e o gerador entrega `ffmpeg-corresponding-source.zip`. Não mude a flag apenas para eliminar o aviso. Nenhuma integração/autenticação GitHub é necessária para os snapshots públicos; a publicação do código continua manual.

O executável x64 funciona nativamente em Windows x64 e por emulação em Windows ARM64 com suporte x64. Windows de 32 bits conserva o gerador Windows legado, com as limitações documentadas no README. O binário é uma entrada externa de build e não é commitado no Git. Antes de `dotnet publish`, coloque aqui a build identificada acima, a licença e os registros de fontes.
