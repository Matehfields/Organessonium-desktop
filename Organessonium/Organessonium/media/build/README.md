# Construção controlada do motor de mídia

O fluxo normal de publicação do Organessônium não recompila FFmpeg. Ele confere `runtime-package.zip`, o manifesto e `sources/ffmpeg-corresponding-source.zip` antes de gerar os artefatos. O ZIP público do aplicativo inclui esses inputs verificados, além do código C#, permitindo `dotnet build` e `dotnet publish` sem preparar uma toolchain nativa.

## Reconstrução do FFmpeg

Requer Windows x64, PowerShell 7, Git, `tar.exe` do Windows e espaço para fontes/objetos. Não instala Docker, WSL, serviços nem altera o PATH global. `Prepare-Environment.ps1 -WorkDirectory <work>` baixa/confere a base MSYS2 fixada por SHA-256 e executa comandos em UCRT64 isolado, com janelas ocultas. `Initialize-Toolchain.ps1` prepara as ferramentas e bibliotecas. É uma preparação de ambiente, não uma promessa de que um repositório rolling terá as mesmas versões no futuro.

Para a revisão exata distribuída, consulte `source-inventory.json` do pacote completo: cada pacote nativo identifica versão e arquivo de fontes assinado. As receitas PKGBUILD, patches e upstreams estão dentro de `package-sources/*.src.tar.zst`. Ferramentas gerais de compilação não são substituídas por fontes das bibliotecas. Instale os pacotes binários dessas versões quando ainda disponíveis, ou reconstrua/instale as bibliotecas com os PKGBUILDs incluídos e `makepkg-mingw`, respeitando suas dependências. Não use uma biblioteca mais nova e conserve o manifesto antigo.

Extraia `ffmpeg-corresponding-source.zip` em `<work>`, preservando os diretórios `sources`, `package-sources`, `vendor` e `build`. Extraia `ffmpeg-source.zip` em `<work>/sources`, obtendo `FFmpeg-b16b5f2a01f3c4f8c9a7769d7a35e8b193946d3e`. `sources/<componente>` já contém as revisões/adaptações e submodules usados; os metadados Git incluídos permitem reproduzir os geradores de versão sem consultar branches atuais.

No ambiente UCRT64, inicie em `<work>` e execute `bash <pacote>/build/build-runtime.sh`. A configuração está integralmente em `ffmpeg-config.txt`; o script acrescenta somente prefixos locais, flags de ligação, mapa de link e identificação explícita da revisão. Até quatro processos de compilação são usados, ajustáveis por `ORG_MEDIA_JOBS`. A compilação produz `compiled/ffmpeg/ffmpeg.exe`, `ffprobe.exe` e os mapas de link. Não depende do Git do repositório C# para identificar a versão do core.

### Rust offline

Rav1e 0.8.1 e libdovi 3.4.0 têm seus Cargo.lock originais nos respectivos arquivos de fontes, além dos vendors completos obtidos com `cargo vendor --locked`. Para cada biblioteca, configure um CARGO_HOME isolado com `[source.crates-io] replace-with = "vendored-sources"` e `[source.vendored-sources] directory = "<work>/vendor/rav1e"` ou `"<work>/vendor/libdovi"`. Use caminhos com `/`, `CARGO_NET_OFFLINE=true` e os comandos `--frozen` dos PKGBUILDs. O caminho absoluto é escolhido pelo reconstrutor; não faz parte do contrato ou de um segredo. Não execute `cargo update`.

## Inventário e pacote

`Collect-PackageSources.ps1` percorre imports PE e mapas de link, resolve os arquivos pelo banco local do pacman, inclui o fechamento das dependências dos pacotes e coleta as fontes assinadas. Headers/build inputs adicionais usam as versões da `.BUILDINFO` do pacote binário, nunca uma suposição baseada no pacote mais recente. `source-review.json` vincula cada revisão manual necessária ao SHA-256 do arquivo de fontes e lista fontes adicionais obrigatórias.

`Package-Runtime.ps1` confere inputs, reúne avisos completos, cria o arquivo embutido e as fontes correspondentes em `<work>/staged-media`. O pacote de fontes inclui este script, PKGBUILDs, upstreams, patches/adaptações, submodules, histórico usado na geração de versões e crates com licenças. O inventário público não contém caminhos da máquina de build. Um pacote novo só deve ser instalado com `-Install` após comparar capacidades com a build anterior e executar as regressões relacionadas de miniatura, prévia e extração/execução embutida.

As versões nativas escolhidas fazem parte da identidade da build. Atualizá-las exige um novo inventário, fontes correspondentes e testes. Não reutilize `redistributionReady` nem hashes de outra build. `Verify-Sources.ps1 -ForRedistribution` e o gerador de releases bloqueiam fontes/binários incompatíveis. A etapa de coleta/empacotamento deve ser executada no checkout desktop, com o layout `media/sources` e os vendors em `source-audit/vendor`; o pacote de fontes isolado usa `vendor` e fornece os inputs para reconstruir as bibliotecas e executar `build-runtime.sh`. Consulte também `../NOTICE.md`.
