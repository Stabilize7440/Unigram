param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$msbuild = 'F:\Dev\Microsoft Visual Studio\community\MSBuild\Current\Bin\amd64\MSBuild.exe'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

function Extract-Block([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing source block: $signature" }
    $body = $source.IndexOf('{', $start)
    $depth = 1
    $end = $body + 1
    while ($end -lt $source.Length -and $depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw 'Unbalanced source block' }
    return $source.Substring($start, $end - $start)
}

$overlay = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\Telegram\Controls\OverlayWindow.cs'))
$opened = Extract-Block $overlay '        public static bool PopupOpened(XamlRoot xamlRoot)'
$closed = Extract-Block $overlay '        public static bool PopupClosed(XamlRoot xamlRoot)'
$hide = Extract-Block $overlay '        public static void TryHide(XamlRoot xamlRoot, ContentDialogResult result)'
$lookup = ''
# Allows the same regression suite to run against the unfixed revision.
if ($overlay.Contains('        private static bool TryGetWindow(')) {
    $lookup = Extract-Block $overlay '        private static bool TryGetWindow('
}
$navigation = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\Telegram\Common\TLNavigationService.cs'))
$reuse = Extract-Block $navigation '                    if (Frame?.Content is ChatPage chatPage && !scheduled && !force)'
$probe = @"
using System.Threading.Tasks;
using Telegram.Controls;
using Telegram.Td.Api;
using Telegram.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
namespace Telegram.Controls {
    public partial class OverlayWindow {
$lookup
$opened
$closed
$hide
    }
}
namespace Telegram.Navigation.Services {
    public partial class NavigationProbe {
        public async Task NavigateAsync(Chat chat, MessageTopic topic = null, NavigationState state = null, bool scheduled = false, bool force = false, bool clearBackStack = false) {
            state ??= new NavigationState();
$reuse
        }
    }
}
"@
$generated = Join-Path $PSScriptRoot 'obj\Navigation.probe.g.cs'
New-Item -ItemType Directory -Force -Path (Split-Path $generated) | Out-Null
[IO.File]::WriteAllText($generated, $probe, [Text.UTF8Encoding]::new($false))

& $msbuild (Join-Path $PSScriptRoot 'Navigation.Tests.csproj') /restore "/p:Configuration=$Configuration" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet (Join-Path $PSScriptRoot "bin\$Configuration\net10.0\Navigation.Tests.dll")
exit $LASTEXITCODE
