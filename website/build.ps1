<#
  Builds the static site into website/_site: every page in every language from layout.html + a page body, with
  i18n/<language>.json for the texts, plus sitemap.xml, robots.txt and the assets. Runs on Windows PowerShell 5.1
  and pwsh.
    ./website/build.ps1
#>
param([string]$Out = (Join-Path $PSScriptRoot '_site'))
$ErrorActionPreference = 'Stop'
$enc = New-Object System.Text.UTF8Encoding($false)
$src = $PSScriptRoot

# Languages: code and Open Graph locale. Portuguese pages live at the site root.
$languages = @(
    @{ Code = 'pt-BR'; Og = 'pt_BR' },
    @{ Code = 'en'; Og = 'en_US' },
    @{ Code = 'es'; Og = 'es_ES' }
)

# Pages: body (home.html is shared and translated with {{t:key}}; guide/<code>.html is written per language),
# URL per language and the i18n keys of their title/description.
$pages = @(
    @{ Id = 'home'; Body = 'home.html'; OgType = 'website'; Title = 'meta.title'; Description = 'meta.description'; OgTitle = 'og.title'
       Paths = @{ 'pt-BR' = '/'; en = '/en/'; es = '/es/' } },
    @{ Id = 'guide'; Body = 'guide'; OgType = 'article'; Title = 'guide.metaTitle'; Description = 'guide.description'; OgTitle = 'guide.title'
       Paths = @{ 'pt-BR' = '/como-transformar-demo-do-cs2-em-video/'; en = '/en/cs2-demo-to-video/'; es = '/es/convertir-demo-de-cs2-en-video/' } }
)
$xDefault = 'en'
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
function PlainText([string]$html) {
    $text = [System.Net.WebUtility]::HtmlDecode(([regex]::Replace($html, '<[^>]+>', ' ')))
    ([regex]::Replace($text, '\s+', ' ')).Trim()
}
# JSON inside <script>: never let "</" close the element early.
function ScriptJson($value) { (ConvertTo-Json $value -Depth 10 -Compress).Replace('</', '<\/') }

$strings = @{}
foreach ($l in $languages) { $strings[$l.Code] = Load-Strings $l.Code }

# Every language must define the same keys.
$reference = $strings['pt-BR'].Keys | Sort-Object
foreach ($l in $languages) {
    $missing = $reference | Where-Object { -not $strings[$l.Code].ContainsKey($_) }
    $extra = $strings[$l.Code].Keys | Where-Object { -not $strings['pt-BR'].ContainsKey($_) }
    if ($missing -or $extra) { throw "i18n/$($l.Code).json: missing [$($missing -join ', ')] extra [$($extra -join ', ')]" }
}

$layout = [IO.File]::ReadAllText((Join-Path $src 'layout.html'), $enc)
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
$homePage = $pages | Where-Object Id -eq 'home'
$guidePage = $pages | Where-Object Id -eq 'guide'

function Translate([string]$html, [hashtable]$t, [string]$code) {
    [regex]::Replace($html, '\{\{([ta]):([A-Za-z0-9_.]+)\}\}', {
        param($m)
        $key = $m.Groups[2].Value
        if (-not $t.ContainsKey($key)) { throw "i18n/$code.json has no '$key'" }
        if ($m.Groups[1].Value -eq 'a') { Attr $t[$key] } else { $t[$key] }
    })
}

function HomeJsonLd([hashtable]$t, [string]$code, [string]$canonical) {
    $faqKeys = @($t.Keys | Where-Object { $_ -match '^q\d+$' } | Sort-Object { [int]$_.Substring(1) })
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
    $faq = [ordered]@{
        '@type' = 'FAQPage'
        inLanguage = $code
        mainEntity = @($faqKeys | ForEach-Object {
            $n = $_.Substring(1)
            [ordered]@{ '@type' = 'Question'; name = (PlainText $t["q$n"]); acceptedAnswer = [ordered]@{ '@type' = 'Answer'; text = (PlainText $t["a$n"]) } }
        })
    }
    @($app, $faq)
}

function GuideJsonLd([hashtable]$t, [string]$code, [string]$canonical, [string]$homeUrl, [string]$body) {
    # Steps are the <h2 data-step id="…"> sections of the guide body.
    $sections = [regex]::Matches($body, '<h2[^>]*\bid="([^"]+)"[^>]*\bdata-step[^>]*>(.*?)</h2>(.*?)(?=<h2|<aside|</article>)', 'Singleline')
    $position = 0
    $steps = @($sections | ForEach-Object {
        $position++
        [ordered]@{
            '@type' = 'HowToStep'
            position = $position
            name = ([regex]::Replace((PlainText $_.Groups[2].Value), '^\d+\.\s*', ''))
            text = (PlainText $_.Groups[3].Value)
            url = "$canonical#$($_.Groups[1].Value)"
        }
    })
    if ($steps.Count -eq 0) { throw "guide/$code.html has no <h2 data-step> sections" }
    $howTo = [ordered]@{
        '@type' = 'HowTo'
        name = $t['guide.title']
        description = $t['guide.description']
        inLanguage = $code
        image = "$site/assets/og.png"
        tool = @([ordered]@{ '@type' = 'HowToTool'; name = 'cs2clutchEz' }, [ordered]@{ '@type' = 'HowToTool'; name = 'Counter-Strike 2' })
        step = $steps
    }
    $crumbs = [ordered]@{
        '@type' = 'BreadcrumbList'
        itemListElement = @(
            [ordered]@{ '@type' = 'ListItem'; position = 1; name = 'cs2clutchEz'; item = $homeUrl },
            [ordered]@{ '@type' = 'ListItem'; position = 2; name = $t['guide.title']; item = $canonical }
        )
    }
    @($howTo, $crumbs)
}

$built = 0
foreach ($page in $pages) {
    $alternates = (@($languages | ForEach-Object { "  <link rel=""alternate"" hreflang=""$($_.Code)"" href=""$site$($page.Paths[$_.Code])"">" }) +
        "  <link rel=""alternate"" hreflang=""x-default"" href=""$site$($page.Paths[$xDefault])"">") -join "`n"
    $banner = @{}
    foreach ($l in $languages) {
        $t = $strings[$l.Code]
        $banner[$l.Code] = @{ text = $t['banner.text']; action = $t['banner.action']; close = $t['banner.close']; url = $page.Paths[$l.Code] }
    }

    foreach ($l in $languages) {
        $code = $l.Code
        $t = $strings[$code]
        $path = $page.Paths[$code]
        $canonical = "$site$path"

        $bodyFile = if ($page.Id -eq 'home') { Join-Path $src $page.Body } else { Join-Path (Join-Path $src $page.Body) "$code.html" }
        $body = [IO.File]::ReadAllText($bodyFile, $enc)

        $faqHtml = ''
        if ($page.Id -eq 'home') {
            $faqKeys = @($t.Keys | Where-Object { $_ -match '^q\d+$' } | Sort-Object { [int]$_.Substring(1) })
            $faqHtml = ($faqKeys | ForEach-Object {
                $n = $_.Substring(1)
                "        <details>`n          <summary>$($t["q$n"])</summary>`n          <p>$($t["a$n"])</p>`n        </details>"
            }) -join "`n"
        }

        $langLinks = ($languages | ForEach-Object {
            $label = $_.Code.Substring(0, 2).ToUpperInvariant()
            $current = if ($_.Code -eq $code) { ' aria-current="page"' } else { '' }
            "        <a href=""$($page.Paths[$_.Code])"" hreflang=""$($_.Code)"" lang=""$($_.Code)""$current>$label</a>"
        }) -join "`n"
        $ogAlternates = ($languages | Where-Object { $_.Code -ne $code } | ForEach-Object { "  <meta property=""og:locale:alternate"" content=""$($_.Og)"">" }) -join "`n"

        $graph = if ($page.Id -eq 'home') { HomeJsonLd $t $code $canonical } else { GuideJsonLd $t $code $canonical "$site$($homePage.Paths[$code])" (Translate $body $t $code) }
        $jsonld = ScriptJson ([ordered]@{ '@context' = 'https://schema.org'; '@graph' = @($graph) })
        $pageData = ScriptJson ([ordered]@{
            lang = $code
            pages = [ordered]@{ 'pt-BR' = $page.Paths['pt-BR']; en = $page.Paths['en']; es = $page.Paths['es'] }
            text = [ordered]@{ invalid = $t['js.invalid']; opening = $t['js.opening']; note = $t['share.note'] }
            banner = $banner
        })

        $html = $layout.Replace('{{content}}', $body).Replace('{{faq}}', $faqHtml)
        $html = $html.Replace('{{lang}}', $code).Replace('{{canonical}}', $canonical).Replace('{{site}}', $site).
            Replace('{{home}}', $homePage.Paths[$code]).Replace('{{guide}}', $guidePage.Paths[$code]).
            Replace('{{guideCurrent}}', $(if ($page.Id -eq 'guide') { ' aria-current="page"' } else { '' })).
            Replace('{{ogType}}', $page.OgType).Replace('{{alternates}}', $alternates).Replace('{{ogLocale}}', $l.Og).
            Replace('{{ogLocaleAlternates}}', $ogAlternates).Replace('{{langLinks}}', $langLinks).
            Replace('{{pageTitle}}', (Attr $t[$page.Title])).Replace('{{pageDescription}}', (Attr $t[$page.Description])).
            Replace('{{pageOgTitle}}', (Attr $t[$page.OgTitle])).Replace('{{jsonld}}', $jsonld).Replace('{{pageData}}', $pageData)
        $html = Translate $html $t $code
        if ($html -match '\{\{[^}]+\}\}') { throw "Unreplaced placeholder in $($page.Id)/$code`: $($Matches[0])" }

        $dir = Join-Path $Out ($path.Trim('/'))
        New-Item -ItemType Directory -Force $dir | Out-Null
        [IO.File]::WriteAllText((Join-Path $dir 'index.html'), $html, $enc)
        $built++
    }
}

# sitemap.xml: every page with the language alternates of its group.
$urls = foreach ($page in $pages) {
    $links = (@($languages | ForEach-Object { "    <xhtml:link rel=""alternate"" hreflang=""$($_.Code)"" href=""$site$($page.Paths[$_.Code])""/>" }) +
        "    <xhtml:link rel=""alternate"" hreflang=""x-default"" href=""$site$($page.Paths[$xDefault])""/>") -join "`n"
    foreach ($l in $languages) { "  <url>`n    <loc>$site$($page.Paths[$l.Code])</loc>`n    <lastmod>$today</lastmod>`n$links`n  </url>" }
}
$sitemap = "<?xml version=""1.0"" encoding=""UTF-8""?>`n<urlset xmlns=""http://www.sitemaps.org/schemas/sitemap/0.9"" xmlns:xhtml=""http://www.w3.org/1999/xhtml"">`n$($urls -join "`n")`n</urlset>`n"
[IO.File]::WriteAllText((Join-Path $Out 'sitemap.xml'), $sitemap, $enc)
[IO.File]::WriteAllText((Join-Path $Out 'robots.txt'), "User-agent: *`nAllow: /`n`nSitemap: $site/sitemap.xml`n", $enc)

Copy-Item (Join-Path $src 'assets') (Join-Path $Out 'assets') -Recurse
foreach ($f in 'CNAME', '.nojekyll', '404.html') { Copy-Item (Join-Path $src $f) $Out }
"Built $built pages (v$version) into $Out"
