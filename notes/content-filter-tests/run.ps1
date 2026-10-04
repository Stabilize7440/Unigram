param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$msbuild = 'F:\Dev\Microsoft Visual Studio\community\MSBuild\Current\Bin\amd64\MSBuild.exe'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\Telegram\Services\ClientService.cs'))
function Extract-Method([string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing method: $signature" }
    $body = $source.IndexOf('{', $start)
    $depth = 1
    $end = $body + 1
    while ($end -lt $source.Length -and $depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw 'Unbalanced method source' }
    return $source.Substring($start, $end - $start)
}
$send = Extract-Method '        public void Send(Function function, Action<Object> handler = null)'
$async = Extract-Method '        public Task<Object> SendAsync(Function function)'
$probe = @"
using System;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Object = Telegram.Td.Api.Object;
namespace Telegram.Services {
    public sealed class ClientServiceSendProbe {
        private readonly FakeNativeClient _client = new();
        public int NativeRequests => _client.Requests;
$send
$async
    }
}
"@
$generated = Join-Path $PSScriptRoot 'obj\ClientService.Send.probe.g.cs'
New-Item -ItemType Directory -Force -Path (Split-Path $generated) | Out-Null
[IO.File]::WriteAllText($generated, $probe, [Text.UTF8Encoding]::new($false))
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\Telegram\Controls\Messages\MessageBubble.xaml.cs'))
$recycleContent = Extract-Method '        private void RecycleContent()'
$recycle = Extract-Method '        public void Recycle()'
$register = Extract-Method '        public void RegisterEvents()'
$unregister = Extract-Method '        public void UnregisterEvents()'
$fold = Extract-Method '        private bool UpdateContentFilter(MessageViewModel message)'
if (-not $fold.Contains('RecycleContent();') -or $fold.Contains('Recycle();')) { throw 'Fold must use content-only cleanup' }
if (-not $source.Contains('message == null || IsContentFiltered || _ignoreSizeChanged')) { throw 'Folded content must not animate to zero size' }
Write-Host 'PASS fold uses content-only cleanup and size-change handler skips collapsed content'
$lifecycle = @"
namespace Telegram.Controls.Messages {
    public sealed class MessageBubbleLifecycleProbe {
        private bool _ignoreSizeChanged = true;
        private readonly ProbeText Message = new();
        private readonly ProbeFooter Footer = new();
        private readonly ProbeContent _content = new();
        private readonly ProbeBorder Media = new();
        public MessageBubbleLifecycleProbe() { Media.Child = _content; }
        public bool IgnoreSizeChanged => _ignoreSizeChanged;
        public int ContentRecycles => _content.Recycles;
        public bool IsContentFiltered { get; set; }
        private void SetContentFilterSubscription(object settings) { }
        public void FoldForProbe() { RecycleContent(); Media.Child = null; IsContentFiltered = true; }
        public void RevealForProbe() { IsContentFiltered = false; }
$recycleContent
$recycle
$register
$unregister
    }
    public interface IContent { void Recycle(); }
    public sealed class ProbeContent : IContent { public int Recycles; public void Recycle() { Recycles++; } }
    public sealed class ProbeText { public void Clear() { } }
    public sealed class ProbeFooter { public void UpdateMessage(object message) { } }
    public sealed class ProbeBorder { public object Child { get; set; } }
}
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'obj\MessageBubble.Lifecycle.probe.g.cs'), $lifecycle, [Text.UTF8Encoding]::new($false))

[xml]$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\Telegram\Controls\Messages\MessageBubble.xaml'))
$namespaces = [Xml.XmlNamespaceManager]::new($template.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$xaml = 'http://schemas.microsoft.com/winfx/2006/xaml'
$controlRoot = $template.SelectSingleNode('//p:ControlTemplate/p:Grid', $namespaces)
$states = $controlRoot.SelectSingleNode('p:VisualStateManager.VisualStateGroups', $namespaces)
if ($null -eq $states) { throw 'Visual states must be attached to the template root' }
$normal = $controlRoot.SelectSingleNode('p:Grid', $namespaces)
if ($normal.GetAttribute('Name', $xaml) -ne 'NormalContent' -or $null -ne $normal.SelectSingleNode('p:VisualStateManager.VisualStateGroups', $namespaces)) { throw 'NormalContent must not own template visual states' }
$stateNames = @($states.SelectNodes('.//p:VisualState', $namespaces) | ForEach-Object { $_.GetAttribute('Name', $xaml) })
foreach ($name in @('Normal', 'LightStateOut', 'LightState', 'MediaState', 'HiddenState')) {
    if ($stateNames -notcontains $name) { throw "Missing original visual state: $name" }
}
Write-Host 'PASS original visual states remain on the template root'
Write-Host 'PASS all five original visual states preserved'

& $msbuild (Join-Path $PSScriptRoot 'ContentFilter.Tests.csproj') /restore "/p:Configuration=$Configuration" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet (Join-Path $PSScriptRoot "bin\$Configuration\net10.0\ContentFilter.Tests.dll")
exit $LASTEXITCODE
