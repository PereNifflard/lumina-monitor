using System.Runtime.Versioning;

// This assembly is Windows-only, and says so once here rather than at every
// call site. It talks to the Apple multiplexer that the "Appareils Apple" app
// installs on Windows, and it decodes video through Media Foundation, which is
// a Windows component; the target framework stays plain net8.0 because nothing
// here needs the Windows desktop SDK, only the platform annotation.
[assembly: SupportedOSPlatform("windows")]
