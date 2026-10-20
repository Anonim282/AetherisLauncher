using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AetherisLauncher.Services
{
    public class JavaInstallation
    {
        public string Path { get; set; } = "";
        public int MajorVersion { get; set; }
        public string Name { get; set; } = "";
        public bool Is64Bit { get; set; } = true;

        public override string ToString() => $"{Name} (Java {MajorVersion}) - {Path}";
    }

    public class JavaDetector
    {
        public static List<JavaInstallation> FindInstalledJavas()
        {
            var results = new List<JavaInstallation>();
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var candidateDirs = new List<string>
            {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\Zulu",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Common\Java")
            };

            foreach (var baseDir in candidateDirs)
            {
                if (!Directory.Exists(baseDir))
                    continue;

                try
                {
                    foreach (var subDir in Directory.GetDirectories(baseDir))
                    {
                        var javaExe = Path.Combine(subDir, "bin", "java.exe");
                        if (File.Exists(javaExe) && scannedPaths.Add(javaExe))
                        {
                            var info = InspectJava(javaExe, Path.GetFileName(subDir));
                            if (info != null)
                                results.Add(info);
                        }
                    }
                }
                catch { }
            }

            // Check PATH environment variable
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var javaExe = Path.Combine(p.Trim(), "java.exe");
                if (File.Exists(javaExe) && scannedPaths.Add(javaExe))
                {
                    var info = InspectJava(javaExe, "System PATH Java");
                    if (info != null)
                        results.Add(info);
                }
            }

            return results.OrderByDescending(j => j.MajorVersion).ToList();
        }

        public static JavaInstallation? GetBestJavaForMinecraft(string mcVersion, List<JavaInstallation> javas)
        {
            if (javas.Count == 0)
                return null;

            int targetMajor = 21;

            if (TryExtractVersionNumber(mcVersion, out int major, out int minor))
            {
                if (minor <= 16)
                    targetMajor = 8;
                else if (minor == 17)
                    targetMajor = 17;
                else if (minor <= 20)
                    targetMajor = 17;
                else
                    targetMajor = 21;
            }

            // Find exact match first
            var exact = javas.FirstOrDefault(j => j.MajorVersion == targetMajor);
            if (exact != null)
                return exact;

            // For versions 1.18 - 1.20.4, restrict max Java to 21 (Java 24 breaks Forge ModLauncher and Unsafe)
            if (targetMajor == 17)
            {
                var java17 = javas.FirstOrDefault(j => j.MajorVersion == 17);
                if (java17 != null) return java17;

                var java21 = javas.FirstOrDefault(j => j.MajorVersion == 21);
                if (java21 != null) return java21;

                var compatible = javas.Where(j => j.MajorVersion >= 17 && j.MajorVersion <= 21).OrderBy(j => j.MajorVersion).FirstOrDefault();
                if (compatible != null) return compatible;
            }
            else if (targetMajor >= 21)
            {
                var exactOrNewer = javas.Where(j => j.MajorVersion >= 21).OrderBy(j => j.MajorVersion).FirstOrDefault();
                if (exactOrNewer != null) return exactOrNewer;
            }

            // Fallback
            return javas.OrderBy(j => j.MajorVersion).First();
        }

        private static bool TryExtractVersionNumber(string mcVersion, out int major, out int minor)
        {
            major = 1;
            minor = 20;
            var parts = mcVersion.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor))
                return true;
            return false;
        }

        private static JavaInstallation? InspectJava(string exePath, string label)
        {
            int version = 0;
            var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exePath)!) ?? "");

            // Quick detect from folder name
            if (dirName.Contains("1.8") || dirName.Contains("jre8") || dirName.Contains("jdk8"))
                version = 8;
            else if (dirName.Contains("17"))
                version = 17;
            else if (dirName.Contains("21"))
                version = 21;
            else if (dirName.Contains("11"))
                version = 11;
            else if (dirName.Contains("24"))
                version = 24;

            if (version == 0)
            {
                try
                {
                    var psi = new ProcessStartInfo(exePath, "-version")
                    {
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var output = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(3000);

                        if (output.Contains("1.8."))
                            version = 8;
                        else if (output.Contains("\"17.") || output.Contains(" 17."))
                            version = 17;
                        else if (output.Contains("\"21.") || output.Contains(" 21."))
                            version = 21;
                        else if (output.Contains("\"11.") || output.Contains(" 11."))
                            version = 11;
                        else if (output.Contains("\"24.") || output.Contains(" 24."))
                            version = 24;
                    }
                }
                catch { }
            }

            if (version == 0)
                version = 17; // fallback assumption

            return new JavaInstallation
            {
                Path = exePath,
                Name = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(Path.GetDirectoryName(exePath)!) : label,
                MajorVersion = version,
                Is64Bit = true
            };
        }
    }
}
