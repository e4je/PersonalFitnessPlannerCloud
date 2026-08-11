# API configuration

The default build-time endpoint is `https://localhost/`. Override it without changing source:

```powershell
.\gradlew.bat assembleDebug -PPFP_API_BASE_URL=https://fitness.example.com/
```

The Gradle configuration rejects an invalid or non-HTTPS `PFP_API_BASE_URL` before compilation. The first-run/settings path validates with the same runtime parser before persisting a URL in DataStore. The client normalizes the trailing slash and rejects HTTP even for `localhost` or a debug build; the manifest also disables cleartext traffic. URLs containing embedded credentials, a query, or a fragment are rejected. Use a trusted TLS certificate in production.

MockWebServer JVM tests use a package-internal factory that explicitly permits HTTP only for the in-process test server. No build variant, Gradle property, or `BuildConfig.DEBUG` path enables this exception in an APK.

The client implements the `/api/v1` endpoints listed in the project specification. It never accepts database credentials and never connects directly to MySQL.
