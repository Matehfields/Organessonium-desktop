# Motor de mídia FFmpeg

O Organessônium chama FFmpeg como programa separado por argumentos e pipes, sem vincular libav ao C#. O runtime, suas DLLs e os avisos são embutidos em `Organessonium.exe`. No primeiro uso, são conferidos por SHA-256 e extraídos por streaming para `%LOCALAPPDATA%\Organessonium\Media\<sha256-do-pacote>`. A preparação roda fora da thread gráfica, coordena processos por lock de arquivo e publica cada arquivo validado por renomeação. Uma nova sessão confere o cache antes de reutilizá-lo. Esse cache contém somente ferramentas e licenças, nunca vídeos, fotos ou chunks do usuário; não usa TEMP.

## Identidade e licença

O manifesto `source-manifest.json` identifica o binário, todas as DLLs/avisos, o arquivo embutido e as fontes correspondentes. A build controlada usa o core FFmpeg `b16b5f2a01f3c4f8c9a7769d7a35e8b193946d3e`, preserva a configuração de codecs anterior e compila componentes adicionais em revisões fixas. Os demais componentes vêm de pacotes MSYS2 UCRT64 identificados por versão, com seus arquivos de fontes e assinaturas conferidos. `build/source-review.json` vincula a revisão das receitas que buscam fontes adicionais aos hashes dos arquivos efetivamente usados. Crates Rust de rav1e/libdovi são incluídos completos, com Cargo.lock e vendor obtido com `cargo vendor --locked`.

`--enable-gpl --enable-version3`, inclusive libx264, torna este FFmpeg GPL-3.0-or-later, não apenas LGPL. A licença está em `GPL-3.0.txt`. `THIRD-PARTY-NOTICES.txt` conserva os avisos e licenças dos componentes. Adaptações das receitas BtbN permanecem sob MIT, com `BtbN-LICENSE.txt`. Esses avisos não atribuem outra licença ao restante do aplicativo.

O pacote completo inclui fontes FFmpeg, fontes/receitas assinadas das dependências, componentes Git fixados com submodules e histórico necessário aos geradores de versão, vendor Rust, adaptações, configuração e instruções de reconstrução. Não depende de logs privados do fornecedor nem de autenticação GitHub. A build anterior do fornecedor não deve ser marcada como conferida por associação com essa build controlada.

## Distribuição

`build-releases.bat` bloqueia releases se `redistributionReady` não for verdadeiro ou se binários, pacote embutido ou fontes divergirem. Não altere essa flag para esconder um aviso. O ZIP público do código inclui o runtime embutido e `Organessonium/media/sources/ffmpeg-corresponding-source.zip`, permitindo compilar/publicar uma extração limpa. O gerador também entrega esse arquivo de fontes separadamente.

No GitHub, publique os instaladores, o EXE portátil e o ZIP de código-fonte gerados para a mesma versão. Preserve o código e as fontes de mídia enquanto os binários correspondentes estiverem disponíveis, sem cobrar acesso adicional às fontes. Não substitua os componentes de uma release já publicada sem publicar também suas novas fontes e identidades.

O MSIXBundle inclui as fontes completas em `media/sources/ffmpeg-corresponding-source.zip` dentro de cada pacote da Store, além dos avisos. Assim, a entrega das fontes não depende de o usuário localizar uma outra página ou solicitar arquivos. Isso aumenta o tamanho do pacote. A Microsoft ainda exige identidade correta, informações da listagem, política de privacidade e aprovação de certificação. Assinatura confiável de instaladores externos também não é fornecida por uma licença open source.

O executável de mídia é x64. Windows ARM64 com suporte x64 usa emulação; Windows 32 bits conserva o fallback de mídia Windows. Não se deve anunciar suporte a codecs de hardware sem o driver/equipamento necessário.

As conferências de integridade/fontes não são certificação jurídica nem licença de patentes. Condições de uso, marcas, patentes de codecs e as regras aplicáveis ao serviço de armazenamento continuam sendo responsabilidade de quem distribui.

Referências oficiais: [FFmpeg](https://ffmpeg.org/legal.html), [GPLv3](https://www.gnu.org/licenses/gpl-3.0.html), [receitas MSYS2](https://github.com/msys2/MINGW-packages), [receitas BtbN](https://github.com/BtbN/FFmpeg-Builds), [políticas da Store](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies).
