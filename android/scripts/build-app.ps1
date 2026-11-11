param(
    [Parameter(Mandatory)][string]$JavaHome,
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$Gradle,
    [string]$Proxy='',
    [switch]$Release
)
$ErrorActionPreference='Stop'
$android=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:JAVA_HOME=$JavaHome
$env:ANDROID_HOME=$Sdk
$sdkValue=$Sdk.Replace('\','/').Replace(':','\:')
"sdk.dir=$sdkValue" | Set-Content -LiteralPath (Join-Path $android 'local.properties') -Encoding utf8
$proxyArguments=@()
if($Proxy){
    $uri=[Uri]$Proxy
    if($uri.Scheme -ne 'http' -or $uri.UserInfo){throw 'Use a local HTTP proxy without embedded credentials for dependency downloads.'}
    $proxyArguments=@("-Dhttps.proxyHost=$($uri.Host)","-Dhttps.proxyPort=$($uri.Port)","-Dhttp.proxyHost=$($uri.Host)","-Dhttp.proxyPort=$($uri.Port)")
}
if($Release){
    $private=Join-Path $android '.private'
    $properties=Join-Path $private 'signing.properties'
    $key=Join-Path $private 'release.jks'
    if(!(Test-Path -LiteralPath $properties) -and !(Test-Path -LiteralPath $key)){
        New-Item -ItemType Directory -Force $private | Out-Null
        $env:REGION_ANDROID_KEY_PASSWORD=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        try {
            & (Join-Path $JavaHome 'bin/keytool.exe') -genkeypair -keystore $key -storetype JKS -alias region-launcher -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Region Launcher Android' -storepass:env REGION_ANDROID_KEY_PASSWORD -keypass:env REGION_ANDROID_KEY_PASSWORD
            if($LASTEXITCODE){throw 'Could not create APK signing key'}
            "password=$env:REGION_ANDROID_KEY_PASSWORD" | Set-Content -LiteralPath $properties -Encoding ascii
        } finally {Remove-Item Env:REGION_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue}
    }
    if(!(Test-Path -LiteralPath $properties) -or !(Test-Path -LiteralPath $key)){throw 'Signing key or password is missing; restore the existing pair instead of replacing it.'}
}
$build=if($Release){':app:assembleRelease'}else{':app:assembleDebug'}
& $Gradle -p $android --no-daemon @proxyArguments $build :app:testDebugUnitTest :app:lintDebug
if($LASTEXITCODE){throw "Android build or verification failed ($LASTEXITCODE)"}
