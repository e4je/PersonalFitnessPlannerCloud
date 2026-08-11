# Release signing

The next Release task will produce an unsigned APK; this source-integration stage contains no new APK. Do not commit a keystore or passwords.

From the Android project root, the unsigned build output is:

```text
app\build\outputs\apk\release\app-release-unsigned.apk
```

After `scripts\copy-artifacts.ps1`, the delivery copy is:

```text
artifacts\PersonalFitnessPlanner-release-unsigned.apk
```

Create a private keystore under the user's local application-data directory, outside this repository. Keep it backed up separately; losing it prevents signing compatible updates.

```powershell
$SigningDir = Join-Path $env:LOCALAPPDATA 'PersonalFitnessPlanner\signing'
New-Item -ItemType Directory -Force -Path $SigningDir | Out-Null
$KeyStore = Join-Path $SigningDir 'personal-fitness-upload.jks'
& "$env:JAVA_HOME\bin\keytool.exe" -genkeypair -v -keystore $KeyStore -alias upload -keyalg RSA -keysize 4096 -validity 10000
```

After `assembleRelease`, use Build Tools from the standard SDK path. `ANDROID_SDK_ROOT` may override the fallback shown here.

```powershell
$ProjectRoot = (Get-Location).Path
$SdkRoot = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$BuildTools = Join-Path $SdkRoot 'build-tools\35.0.0'
$UnsignedApk = Join-Path $ProjectRoot 'app\build\outputs\apk\release\app-release-unsigned.apk'
$AlignedApk = Join-Path $env:TEMP 'PersonalFitnessPlanner-release-aligned.apk'
$SignedApk = Join-Path $ProjectRoot 'artifacts\PersonalFitnessPlanner-release.apk'
$KeyStore = Join-Path $env:LOCALAPPDATA 'PersonalFitnessPlanner\signing\personal-fitness-upload.jks'

& (Join-Path $BuildTools 'zipalign.exe') -p -f 4 $UnsignedApk $AlignedApk
& (Join-Path $BuildTools 'apksigner.bat') sign --ks $KeyStore --out $SignedApk $AlignedApk
& (Join-Path $BuildTools 'apksigner.bat') verify --verbose --print-certs $SignedApk
```

Supply passwords interactively or through a protected CI secret store; never place them in Gradle properties committed to the repository. A future `app-release-unsigned.apk` is not installable until signed. Tokens used by the installed app are encrypted with a non-exportable Android Keystore key and are excluded from Android system backup.
