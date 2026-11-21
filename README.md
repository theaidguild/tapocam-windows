# TapoCam para Windows

Veja, fotografe e grave câmeras TP-Link Tapo e outras câmeras RTSP na rede local. O TapoCam tem interface em português e funciona na área de notificação do Windows, mesmo com a janela principal fechada.

## Requisitos

- Windows 10 21H2 ou mais recente, ou Windows 11, **x64**.
- [Microsoft Edge WebView2 Runtime (Evergreen)](https://developer.microsoft.com/microsoft-edge/webview2/#download). O instalador o baixa e instala se estiver ausente.
- Câmera com suporte a RTSP acessível pelo PC na rede local.

O instalador inclui o runtime do .NET e o go2rtc e instala o [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) com o `winget` (que já vem no Windows 10 e 11), se ele ainda não estiver instalado.

## Instalação

1. Em [Releases](https://github.com/theaidguild/tapocam-windows/releases), baixe `TapoCam-<versão>-win-x64-setup.exe`. O arquivo `Source code (zip)` não é o aplicativo.
2. Execute o instalador. Ele instala o TapoCam só para o seu usuário, sem pedir permissão de administrador. Se o WebView2 estiver ausente, o instalador precisa de internet para baixá-lo da Microsoft. Se o FFmpeg não for encontrado, a opção **Instalar o FFmpeg com o winget** vem marcada e uma janela mostra o progresso do `winget`.
3. Abra o TapoCam pelo menu Iniciar. As configurações são mantidas ao atualizar.

Para verificar atualizações, clique com o botão direito no ícone do TapoCam na bandeja e escolha **Verificar atualizações…**. Se uma nova versão estiver disponível, você pode aceitar o download e a instalação silenciosa; o aplicativo será reiniciado ao terminar. Atualizações não começam enquanto houver uma gravação ativa. O TapoCam verifica a integridade do instalador usando o SHA-256 informado pelo GitHub Releases. Esta opção atualiza instalações feitas pelo instalador; versões portáteis devem ser substituídas manualmente.

Também há um ZIP portátil, `TapoCam-<versão>-win-x64.zip`, que não instala o WebView2 nem inclui o FFmpeg: instale o WebView2 se estiver ausente e execute `winget install Gyan.FFmpeg.Essentials` antes de usar.

## Primeiros passos

1. No aplicativo Tapo do celular, abra as configurações da câmera e crie uma **Conta da Câmera** em **Configurações → Avançadas**. Esse usuário e essa senha são diferentes da conta TP-Link usada para entrar no app do celular.
2. No TapoCam, abra **Ajustes → Câmeras**, clique em **+** e informe nome, IP local, usuário e senha da câmera. Comece com porta RTSP **554** e **25 quadros por segundo**; ajuste se sua câmera usar outros valores.
3. Use **Testar conexão**, salve e volte à janela principal para conectar a câmera. Em **Detalhes do diagnóstico**, consulte ou copie as etapas, os tempos e a saída do FFmpeg; URLs RTSP são ocultadas nessa saída. Evite testar a conexão durante uma gravação importante: o teste abre uma sessão RTSP diretamente com a câmera.

Para câmeras de outras marcas, como Intelbras/Mibo, em **Ajustes → Câmeras** selecione **URL RTSP** em **Tipo de conexão** e cole a URL completa fornecida pela câmera. Por exemplo: `rtsp://usuario:senha@ip-da-camera:554/cam/realmonitor?channel=1&subtype=0`. A URL já inclui os dados de conexão; não é necessário preencher IP, porta, usuário ou senha separadamente. A mesma transmissão é usada nas opções HD e SD.

## Uso

- **Visualização:** a grade mostra prévias SD sem som. Selecione uma câmera para ver em foco, alternar entre HD e SD, gravar ou tirar uma foto. Em **Ajustes → Geral**, escolha entre visualização com áudio (HLS, alguns segundos de atraso) e visualização sem áudio (MSE, atraso mínimo).
- **Gravação e fotos:** use **Gravar** e o botão de foto na câmera em foco. Em **Gravações**, filtre por câmera, data e tipo de arquivo; abra a mídia ou localize-a no Explorador.
- **Agenda:** em **Ajustes → Agenda**, escolha dias, hora inicial, duração, câmeras, qualidade e áudio. Para gravar no horário, o TapoCam precisa estar em execução e o PC precisa estar ligado e fora do modo de suspensão. Fechar a janela mantém o app na bandeja; **Sair do TapoCam** encerra o app.
- **Retenção:** desativada por padrão. Se ativada, exclui permanentemente os vídeos antigos `tapo_*.mp4` na pasta de gravações, mas não as fotos nem as gravações em andamento.

## Dados e privacidade

| Dados | Local |
|---|---|
| Configurações e agenda | `%APPDATA%\TapoCam\settings.json` |
| Senhas das câmeras Tapo e URLs RTSP personalizadas | Gerenciador de Credenciais do Windows, entradas `TapoCam:<GUID>` do usuário |
| Configuração gerada do go2rtc, logs e dados do WebView2 | `%LOCALAPPDATA%\TapoCam\` |
| Gravações e fotos | `%USERPROFILE%\Videos\TapoCam\` por padrão; altere em **Ajustes → Geral** |

O TapoCam reproduz e grava localmente. O relay usa apenas a interface de loopback (`127.0.0.1`, portas `11984`, `18554` e `18555`); não encaminhe essas portas no roteador. O RTSP padrão não é criptografado: use uma rede local confiável. O `go2rtc.yaml` gerado contém credenciais RTSP, mesmo que o `settings.json` não salve senhas. Trate também logs, fotos e vídeos como dados sensíveis; não os publique sem revisar e remover informações privadas.

## Solução de problemas

- **Não conecta à câmera:** confira o endereço, a porta RTSP (normalmente 554) e as credenciais. Se usar uma URL RTSP, confira também o caminho e os parâmetros. Coloque PC e câmera em redes que permitam comunicação entre si; redes de convidados podem isolar os dispositivos. Verifique o firewall sem desativá-lo e não exponha a câmera à internet.
- **Vídeo em branco ou aviso do WebView2:** instale ou repare o [WebView2 Runtime Evergreen](https://developer.microsoft.com/microsoft-edge/webview2/#download) e reinicie o TapoCam. Se o relay não iniciar, verifique `%LOCALAPPDATA%\TapoCam\go2rtc.log` e se outro processo já usa uma das portas locais.
- **Aviso do SmartScreen:** confirme a origem do download na página oficial de Releases antes de tomar qualquer decisão. Não desative o SmartScreen nem ignore avisos de arquivos de origem desconhecida.
- **"ffmpeg não encontrado":** a instalação automática do FFmpeg falhou ou foi desmarcada. Execute `winget install Gyan.FFmpeg.Essentials` no PowerShell e reinicie o TapoCam.
- **Antivírus bloqueou `ffmpeg.exe`:** pode ser um falso positivo, mas verifique a origem do arquivo e peça a análise ao fornecedor do antivírus ou à sua equipe de TI. Não desative a proteção nem crie uma exclusão ampla.

## Compilar e testar

No Windows, com o SDK do **.NET 8**, **PowerShell 7** e **Inno Setup 6** (`winget install JRSoftware.InnoSetup`):

```powershell
dotnet build TapoCam.sln --configuration Release
dotnet test TapoCam.sln --configuration Release --no-build
pwsh -File ./build.ps1
```

O script testa a solução e gera em `dist\` o instalador `TapoCam-<versão>-win-x64-setup.exe` e o ZIP portátil `TapoCam-<versão>-win-x64.zip`, sem criar tags nem publicar releases. Por padrão usa a propriedade `Version` avaliada pelo MSBuild (`VersionPrefix` em `Directory.Build.props`); `-Version 1.0.0` altera tanto a versão publicada quanto os nomes dos arquivos. Os testes de Core também podem ser executados fora do Windows com `dotnet test tests/TapoCam.Core.Tests`; os de App precisam do Windows.

### Distribuir um release

Cada push na `main` que passa no CI publica automaticamente um novo Release (com instalador e ZIP), incrementando o patch do último Release publicado (por exemplo, `v1.0.0` → `v1.0.1`). Para mudar a versão major/minor, aumente `VersionPrefix` em `Directory.Build.props`; ela é usada quando for maior que o último Release. Enviar manualmente uma tag `v<versão>` também dispara o fluxo de release. O atualizador consulta os Releases públicos de `theaidguild/tapocam-windows`; a API do GitHub fornece o SHA-256 dos assets. O go2rtc v1.9.15 (MIT) é baixado de um release específico e verificado com SHA-256. O FFmpeg não é distribuído com o TapoCam; o instalador o obtém pelo `winget` diretamente do publicador. Consulte [os avisos de terceiros](packaging/licenses/THIRD-PARTY-NOTICES.txt).
