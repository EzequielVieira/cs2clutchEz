<#
  Builds the static site into website/_site: one page per language (/, /en/, /es/) from template.html and
  i18n/<language>.json, plus sitemap.xml, robots.txt and the assets. Runs on Windows PowerShell 5.1 and pwsh.
    ./website/build.ps1
#>
param([string]$Out = (Join-Path $PSScriptRoot '_site'))
$ErrorActionPreference = 'Stop'
$enc = New-Object System.Text.UTF8Encoding($false)
$src = $PSScriptRoot

# Languages: code, path, Open Graph locale. Portuguese is the default page at the site root.
$languages = @(
    @{ Code = 'pt-BR'; Path = '/'; Og = 'pt_BR' },
    @{ Code = 'en'; Path = '/en/'; Og = 'en_US' },
    @{ Code = 'es'; Path = '/es/'; Og = 'es_ES' }
)
$xDefault = '/en/'
$site = 'https://' + ([IO.File]::ReadAllText((Join-Path $src 'CNAME'), $enc).Trim())
$props = [IO.File]::ReadAllText((Join-Path (Split-Path $src -Parent) 'Directory.Build.props'), $enc)
$version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

function Load-Strings([string]$code) {
    $table = @{}
    $json = [IO.File]::ReadAllText((Join-Path (Join-Path $src 'i18n') "$code.json"), $enc) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $table[$p.Name] = [string]$p.Value }
    $table
}

function Attr([string]$s) { $s.Replace('&', '&amp;').Replace('"', '&quot;').Replace('<', '&lt;').Replace('>', '&gt;') }
function PlainText([string]$html) { [System.Net.WebUtility]::HtmlDecode(([regex]::Replace($html, '<[^>]+>', ''))) }
# JSON inside <script>: never let "</" close the element early.
function ScriptJson($value) { (ConvertTo-Json $value -Depth 8 -Compress).Replace('</', '<\/') }

$strings = @{}
foreach ($l in $languages) { $strings[$l.Code] = Load-Strings $l.Code }

# Every language must define the same keys.
$reference = $strings['pt-BR'].Keys | Sort-Object
foreach ($l in $languages) {
    $missing = $reference | Where-Object { -not $strings[$l.Code].ContainsKey($_) }
    $extra = $strings[$l.Code].Keys | Where-Object { -not $strings['pt-BR'].ContainsKey($_) }
    if ($missing -or $extra) { throw "i18n/$($l.Code).json: missing [$($missing -join ', ')] extra [$($extra -join ', ')]" }
}

$template = [IO.File]::ReadAllText((Join-Path $src 'template.html'), $enc)
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null

$alternates = (@($languages | ForEach-Object { "  <link rel=""alternate"" hreflang=""$($_.Code)"" href=""$site$($_.Path)"">" }) +
    "  <link rel=""alternate"" hreflang=""x-default"" href=""$site$xDefault"">") -join "`n"
$banner = @{}
foreach ($l in $languages) {
    $t = $strings[$l.Code]
    $banner[$l.Code] = @{ text = $t['banner.text']; action = $t['banner.action']; close = $t['banner.close']; url = $l.Path }
}

foreach ($l in $languages) {
    $t = $strings[$l.Code]
    $canonical = "$site$($l.Path)"

    $faqKeys = @($t.Keys | Where-Object { $_ -match '^q\d+$' } | Sort-Object { [int]$_.Substring(1) })
    $faqHtml = ($faqKeys | ForEach-Object {
        $n = $_.Substring(1)
        "        <details>`n          <summary>$($t["q$n"])</summary>`n          <p>$($t["a$n"])</p>`n        </details>"
    }) -join "`n"

    $langLinks = ($languages | ForEach-Object {
        $label = $_.Code.Substring(0, 2).ToUpperInvariant()
        $current = if ($_.Code -eq $l.Code) { ' aria-current="page"' } else { '' }
        "        <a href=""$($_.Path)"" hreflang=""$($_.Code)"" lang=""$($_.Code)""$current>$label</a>"
    }) -join "`n"

    $ogAlternates = ($languages | Where-Object { $_.Code -ne $l.Code } | ForEach-Object { "  <meta property=""og:locale:alternate"" content=""$($_.Og)"">" }) -join "`n"

    $app = [ordered]@{
        '@type' = 'SoftwareApplication'
        name = 'cs2clutchEz'
        url = $canonical
        description = $t['meta.description']
        applicationCategory = 'MultimediaApplication'
        operatingSystem = 'Windows 10, Windows 11'
        softwareVersion = $version
        downloadUrl = 'https://github.com/EzequielVieira/cs2clutchEz/releases/latest'
        license = 'https://opensource.org/licenses/MIT'
        isAccessibleForFree = $true
        inLanguage = @($languages | ForEach-Object { $_.Code })
        image = "$site/assets/og.png"
        offers = [ordered]@{ '@type' = 'Offer'; price = '0'; priceCurrency = 'USD' }
        author = [ordered]@{ '@type' = 'Person'; name = 'EzequielVieira'; url = 'https://github.com/EzequielVieira' }
        sameAs = @('https://github.com/EzequielVieira/cs2clutchEz')
    }
    $faqLd = [ordered]@{
        '@type' = 'FAQPage'
        inLanguage = $l.Code
        mainEntity = @($faqKeys | ForEach-Object {
            $n = $_.Substring(1)
            [ordered]@{ '@type' = 'Question'; name = (PlainText $t["q$n"]); acceptedAnswer = [ordered]@{ '@type' = 'Answer'; text = (PlainText $t["a$n"]) } }
        })
    }
    $jsonld = ScriptJson ([ordered]@{ '@context' = 'https://schema.org'; '@graph' = @($app, $faqLd) })

    $pageData = ScriptJson ([ordered]@{
        lang = $l.Code
        pages = [ordered]@{ 'pt-BR' = '/'; en = '/en/'; es = '/es/' }
        text = [ordered]@{ invalid = $t['js.invalid']; opening = $t['js.opening']; note = $t['share.note'] }
        banner = $banner
    })

    $html = $template.Replace('{{lang}}', $l.Code).Replace('{{canonical}}', $canonical).Replace('{{site}}', $site).
        Replace('{{home}}', $l.Path).Replace('{{alternates}}', $alternates).Replace('{{ogLocale}}', $l.Og).
        Replace('{{ogLocaleAlternates}}', $ogAlternates).Replace('{{langLinks}}', $langLinks).Replace('{{faq}}', $faqHtml).
        Replace('{{jsonld}}', $jsonld).Replace('{{pageData}}', $pageData)
    $html = [regex]::Replace($html, '\{\{([ta]):([A-Za-z0-9_.]+)\}\}', {
        param($m)
        $key = $m.Groups[2].Value
        if (-not $t.ContainsKey($key)) { throw "i18n/$($l.Code).json has no '$key'" }
        if ($m.Groups[1].Value -eq 'a') { Attr $t[$key] } else { $t[$key] }
    })
    if ($html -match '\{\{[^}]+\}\}') { throw "Unreplaced placeholder in $($l.Code): $($Matches[0])" }

    $dir = Join-Path $Out ($l.Path.Trim('/'))
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'index.html'), $html, $enc)
}

# sitemap.xml with the language alternates of every page.
$links = (@($languages | ForEach-Object { "    <xhtml:link rel=""alternate"" hreflang=""$($_.Code)"" href=""$site$($_.Path)""/>" }) +
    "    <xhtml:link rel=""alternate"" hreflang=""x-default"" href=""$site$xDefault""/>") -join "`n"
$urls = ($languages | ForEach-Object { "  <url>`n    <loc>$site$($_.Path)</loc>`n    <lastmod>$today</lastmod>`n$links`n  </url>" }) -join "`n"
$sitemap = "<?xml version=""1.0"" encoding=""UTF-8""?>`n<urlset xmlns=""http://www.sitemaps.org/schemas/sitemap/0.9"" xmlns:xhtml=""http://www.w3.org/1999/xhtml"">`n$urls`n</urlset>`n"
[IO.File]::WriteAllText((Join-Path $Out 'sitemap.xml'), $sitemap, $enc)
[IO.File]::WriteAllText((Join-Path $Out 'robots.txt'), "User-agent: *`nAllow: /`n`nSitemap: $site/sitemap.xml`n", $enc)

Copy-Item (Join-Path $src 'assets') (Join-Path $Out 'assets') -Recurse
foreach ($f in 'CNAME', '.nojekyll', '404.html') { Copy-Item (Join-Path $src $f) $Out }
"Built $($languages.Count) pages (v$version) into $Out"
