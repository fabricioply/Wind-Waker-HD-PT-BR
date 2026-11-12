# Wind Waker HD PT-BR

Tradução de fãs para **The Legend of Zelda: The Wind Waker HD**, destinada ao **ZeldaWWHDRecomp** no Windows x64.

Versão atual: **1.0.9**. O patch contém 5.040 mensagens em 68 arquivos de texto, incluindo diálogos, itens e interface, e acrescenta glifos acentuados às fontes necessárias.

## Instalar pelo executável

1. Baixe **WindWakerHD-PTBR-Instalador-v1.0.9.exe** em [Releases](https://github.com/fabricioply/Wind-Waker-HD-PT-BR/releases/latest).
2. Feche o jogo e execute o instalador.
3. Confirme a pasta detectada ou selecione a instalação do Recomp manualmente.
4. Clique em **Instalar tradução**.
5. Use o idioma **inglês** nas configurações do jogo, pois a tradução substitui o pacote de textos em inglês.

O instalador verifica sozinho se há uma tradução mais nova nas Releases e pergunta se você quer atualizar (também há o botão **Verificar atualizações**). O executável inclui os arquivos necessários, sem exigir Python ou uma instalação separada do .NET. Faz cópia de segurança, instala pelo ModManager e oferece a opção de restaurar o idioma original. Os saves não são alterados pelo instalador.

## Gerar o mod a partir dos arquivos deste repositório

Requer Python 3 e o pacote original da sua instalação do jogo. O patcher usa somente a biblioteca padrão do Python.

Na pasta do repositório, execute no PowerShell, ajustando o caminho de entrada:

```powershell
python patch/apply_ptbr_patch.py --input "C:/Recomp Library/zeldawwhdrecomp/data/game/content/Common/Pack/permanent_2d_UsEnglish.pack"
```

O pacote traduzido será gerado em:

```text
mods/wind-waker-ptbr/content/Common/Pack/permanent_2d_UsEnglish.pack
```

Com o jogo fechado, copie a pasta `mods/wind-waker-ptbr` para `data/user/ModManager/Mods/` da instalação do Recomp. Ative **wind-waker-ptbr** no perfil usado pelo ModManager e mantenha o idioma inglês. Para voltar ao inglês, desative o mod.

O arquivo original permanece na pasta do jogo; o patch gera uma cópia traduzida para o mod.

## Conteúdo do repositório

```text
patch/
  apply_ptbr_patch.py             # Geração do pacote e das fontes acentuadas
  ptbr_patch.json                 # Textos traduzidos e comandos internos
mods/wind-waker-ptbr/
  manifest.json                  # Identificação do mod
README.md
.gitignore
```

Executáveis ficam exclusivamente nos Releases. Não estão incluídos saves, backups pessoais, ROMs, executáveis do jogo, SDKs ou o pacote original do jogo.

## Correções da versão 1.0.6

- Reorganiza as quebras de linha de 3.044 mensagens, incluindo 27 cartas, considerando largura dos caracteres, estilos das caixas, mudanças de tamanho e variáveis.
- Mantém todas as palavras, comandos internos e opções de resposta da localização atual.
- Inclui a localização atualizada dos nomes e termos do jogo.
- Preserva a correção de rotação das páginas das duas fontes da versão 1.0.5.

- Corrige os acentos da fonte grande, incluindo o “ã” em “Botão”.
- Preserva a correção que converte os marcadores `{{TAG:…}}` em comandos binários.
- Preserva a correção da estrutura das fontes.

O pacote passou em oito testes de fontes, quatro de mensagens e seis de formatação, com conferência das 5.040 mensagens e dos 14.514 comandos internos. Essa validação dos arquivos não substitui a revisão visual de todas as telas e diálogos dentro do jogo.

SHA-256 do pacote traduzido gerado pela versão 1.0.6:

```text
394f921e80bc1e2b076c13cab6b449b762efdea6c30d5caddc07ea4519467527
```

Projeto independente de fãs, sem vínculo com a Nintendo.
