using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScheduledRestart.Core
{
    /// <summary>What a task action does to the machine. Higher values win when a task has several actions.</summary>
    public enum PowerAction
    {
        None = 0,
        Shutdown = 1,
        Restart = 2
    }

    public sealed class Classification
    {
        public Classification(PowerAction action, bool detectedInScript)
        {
            Action = action;
            DetectedInScript = detectedInScript;
        }

        public PowerAction Action { get; private set; }

        /// <summary>True when the restart/shutdown command was found inside a .bat/.cmd/.ps1 file.</summary>
        public bool DetectedInScript { get; private set; }
    }

    /// <summary>
    /// Decides whether a task action restarts or shuts down the computer. Pure logic: the only I/O is
    /// through the <c>readScript(path, workingDirectory)</c> callback, which returns file text or null.
    /// </summary>
    public static class CommandClassifier
    {
        private const int MaxDepth = 5;
        private static readonly string[] ScriptExtensions = { ".bat", ".cmd", ".ps1" };
        private static readonly string[] PowerShellValueParameters =
        {
            "executionpolicy", "windowstyle", "workingdirectory", "version", "outputformat", "inputformat",
            "configurationname", "psconsolefile", "custompipename", "settingsfile", "encodedarguments"
        };

        public static Classification Classify(string command, string arguments, string workingDirectory, Func<string, string, string> readScript)
        {
            var ctx = new Context(workingDirectory, readScript);
            List<string> args = Tokenize(arguments ?? string.Empty);
            PowerAction action = Analyze(command ?? string.Empty, args, ctx, 0, false);
            return new Classification(action, action != PowerAction.None && ctx.FoundInScript);
        }

        private sealed class Context
        {
            public Context(string workingDirectory, Func<string, string, string> readScript)
            {
                WorkingDirectory = workingDirectory;
                ReadScript = readScript ?? ((p, w) => null);
            }

            public string WorkingDirectory { get; private set; }
            public Func<string, string, string> ReadScript { get; private set; }
            public bool FoundInScript { get; set; }
        }

        private static PowerAction Analyze(string program, List<string> args, Context ctx, int depth, bool insideScript)
        {
            if (depth > MaxDepth) return PowerAction.None;
            PowerAction result = AnalyzeCore(program, args, ctx, depth);
            if (result != PowerAction.None && insideScript) ctx.FoundInScript = true;
            return result;
        }

        private static PowerAction AnalyzeCore(string program, List<string> args, Context ctx, int depth)
        {
            string path = Unquote(program).Trim();
            string name = ProgramName(path);
            switch (name)
            {
                case "shutdown":
                    return ClassifyShutdown(args);
                case "psshutdown":
                case "psshutdown64":
                    return ClassifyPsShutdown(args);
                case "wmic":
                    return args.Any(a => Regex.IsMatch(a, @"\breboot\b", RegexOptions.IgnoreCase)) ? PowerAction.Restart : PowerAction.None;
                case "restart-computer":
                    return PowerAction.Restart;
                case "stop-computer":
                    return PowerAction.Shutdown;
                case "cmd":
                    return ClassifyCmd(args, ctx, depth);
                case "powershell":
                case "pwsh":
                    return ClassifyPowerShell(args, ctx, depth);
                case "call":
                case "start":
                case "start-process":
                case "saps":
                    return ClassifyLauncher(name, args, ctx, depth);
            }

            string extension = SafeExtension(path);
            if (ScriptExtensions.Contains(extension))
            {
                string text = ctx.ReadScript(path, ctx.WorkingDirectory);
                if (text == null) return PowerAction.None;
                return ClassifyScriptText(text, extension == ".ps1", ctx, depth + 1, true);
            }
            return PowerAction.None;
        }

        private static PowerAction ClassifyShutdown(List<string> args)
        {
            var switches = new HashSet<string>(args
                .Where(a => a.Length > 1 && (a[0] == '/' || a[0] == '-'))
                .Select(a => a.Substring(1).ToLowerInvariant()));
            if (switches.Overlaps(new[] { "a", "l", "h", "?" })) return PowerAction.None;
            if (switches.Overlaps(new[] { "r", "g" })) return PowerAction.Restart;
            if (switches.Overlaps(new[] { "s", "p", "sg" })) return PowerAction.Shutdown;
            return PowerAction.None;
        }

        private static PowerAction ClassifyPsShutdown(List<string> args)
        {
            var switches = new HashSet<string>(args
                .Where(a => a.Length > 1 && (a[0] == '/' || a[0] == '-'))
                .Select(a => a.Substring(1).ToLowerInvariant()));
            if (switches.Contains("a")) return PowerAction.None;
            if (switches.Contains("r")) return PowerAction.Restart;
            if (switches.Overlaps(new[] { "s", "k" })) return PowerAction.Shutdown;
            return PowerAction.None;
        }

        private static PowerAction ClassifyCmd(List<string> args, Context ctx, int depth)
        {
            int index = args.FindIndex(a => a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index == args.Count - 1) return PowerAction.None;
            string text = JoinTokens(args.Skip(index + 1));
            return ClassifyScriptText(text, false, ctx, depth + 1, false);
        }

        private static PowerAction ClassifyPowerShell(List<string> args, Context ctx, int depth)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                if (token.Length > 1 && (token[0] == '-' || token[0] == '/'))
                {
                    string p = token.TrimStart('-', '/').ToLowerInvariant();
                    bool hasValue = i + 1 < args.Count;
                    if (p == "e" || p == "ec" || (p.Length >= 2 && p.StartsWith("en", StringComparison.Ordinal) && "encodedcommand".StartsWith(p, StringComparison.Ordinal)))
                    {
                        string decoded = hasValue ? DecodeEncodedCommand(args[i + 1]) : null;
                        return decoded == null ? PowerAction.None : ClassifyScriptText(decoded, true, ctx, depth + 1, false);
                    }
                    if (p == "c" || (p.Length >= 3 && "command".StartsWith(p, StringComparison.Ordinal)))
                    {
                        return hasValue ? ClassifyScriptText(JoinTokens(args.Skip(i + 1)), true, ctx, depth + 1, false) : PowerAction.None;
                    }
                    if (p == "f" || (p.Length >= 2 && "file".StartsWith(p, StringComparison.Ordinal)))
                    {
                        return hasValue ? Analyze(args[i + 1], args.Skip(i + 2).ToList(), ctx, depth + 1, false) : PowerAction.None;
                    }
                    if (p == "w" || p == "v" || p == "o" || p == "i" || (p.Length >= 2 && PowerShellValueParameters.Any(v => v.StartsWith(p, StringComparison.Ordinal))))
                    {
                        i++;
                    }
                    continue;
                }

                // First positional argument: a script file, or (Windows PowerShell) an inline command.
                if (SafeExtension(Unquote(token)) == ".ps1")
                    return Analyze(token, args.Skip(i + 1).ToList(), ctx, depth + 1, false);
                return ClassifyScriptText(JoinTokens(args.Skip(i)), true, ctx, depth + 1, false);
            }
            return PowerAction.None;
        }

        private static PowerAction ClassifyLauncher(string name, List<string> args, Context ctx, int depth)
        {
            var rest = new List<string>(args);
            if (name == "start")
            {
                // start ["title"] [/switches] program args
                while (rest.Count > 0 && (rest[0].Length == 0 || rest[0].StartsWith("/", StringComparison.Ordinal))) rest.RemoveAt(0);
                return rest.Count == 0 ? PowerAction.None : Analyze(rest[0], rest.Skip(1).ToList(), ctx, depth + 1, false);
            }
            if (name == "call")
                return rest.Count == 0 ? PowerAction.None : Analyze(rest[0], rest.Skip(1).ToList(), ctx, depth + 1, false);

            // Start-Process [-FilePath] program [-ArgumentList] args
            string file = null;
            var argumentList = new List<string>();
            for (int i = 0; i < rest.Count; i++)
            {
                string p = rest[i].StartsWith("-", StringComparison.Ordinal) ? rest[i].TrimStart('-').ToLowerInvariant() : null;
                if (p != null && p.Length > 0 && "filepath".StartsWith(p, StringComparison.Ordinal) && i + 1 < rest.Count)
                {
                    file = rest[++i];
                }
                else if (p != null && p.Length > 0 && ("argumentlist".StartsWith(p, StringComparison.Ordinal) || p == "args") && i + 1 < rest.Count)
                {
                    foreach (string part in rest[++i].Split(','))
                        argumentList.AddRange(Tokenize(part.Trim().Trim('\'', '"')));
                }
                else if (p == null && file == null)
                {
                    file = rest[i];
                }
                else if (p == null)
                {
                    foreach (string part in rest[i].Split(','))
                        argumentList.AddRange(Tokenize(part.Trim().Trim('\'', '"')));
                }
            }
            return file == null ? PowerAction.None : Analyze(file, argumentList, ctx, depth + 1, false);
        }

        /// <summary>Classifies batch or PowerShell text: one statement per line or separator.</summary>
        private static PowerAction ClassifyScriptText(string text, bool powerShell, Context ctx, int depth, bool insideScript)
        {
            if (depth > MaxDepth || string.IsNullOrWhiteSpace(text)) return PowerAction.None;
            text = text.Trim();
            if (text.Length > 1 && text[0] == '"' && text[text.Length - 1] == '"' && text.IndexOf('"', 1) == text.Length - 1)
                text = text.Substring(1, text.Length - 2);

            PowerAction best = PowerAction.None;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || IsComment(line, powerShell)) continue;

                if (powerShell)
                {
                    if (Regex.IsMatch(line, @"\bRestart-Computer\b", RegexOptions.IgnoreCase)
                        || (Regex.IsMatch(line, @"Win32_OperatingSystem", RegexOptions.IgnoreCase) && Regex.IsMatch(line, @"\bReboot\b", RegexOptions.IgnoreCase)))
                        best = Max(best, Mark(PowerAction.Restart, ctx, insideScript));
                    else if (Regex.IsMatch(line, @"\bStop-Computer\b", RegexOptions.IgnoreCase))
                        best = Max(best, Mark(PowerAction.Shutdown, ctx, insideScript));
                }

                foreach (string statement in SplitStatements(line, powerShell))
                {
                    List<string> tokens = Tokenize(statement);
                    while (tokens.Count > 0 && (tokens[0] == "&" || tokens[0] == "." || tokens[0] == "@"))
                        tokens.RemoveAt(0);
                    if (tokens.Count > 0 && tokens[0].StartsWith("@", StringComparison.Ordinal))
                        tokens[0] = tokens[0].Substring(1);
                    if (tokens.Count == 0 || tokens[0].Length == 0) continue;
                    best = Max(best, Analyze(tokens[0], tokens.Skip(1).ToList(), ctx, depth + 1, insideScript));
                    if (best == PowerAction.Restart) return best;
                }
            }
            return best;
        }

        private static PowerAction Mark(PowerAction action, Context ctx, bool insideScript)
        {
            if (insideScript) ctx.FoundInScript = true;
            return action;
        }

        private static bool IsComment(string line, bool powerShell)
        {
            if (powerShell) return line.StartsWith("#", StringComparison.Ordinal);
            string l = line.TrimStart('@').ToLowerInvariant();
            return l.StartsWith("::", StringComparison.Ordinal) || l == "rem" || l.StartsWith("rem ", StringComparison.Ordinal);
        }

        private static IEnumerable<string> SplitStatements(string line, bool powerShell)
        {
            var current = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    current.Append(c);
                    continue;
                }
                if (c == '"' || (powerShell && c == '\''))
                {
                    quote = c;
                    current.Append(c);
                    continue;
                }
                bool separator = c == '|' || (c == ';' && powerShell) || (c == '&' && !(powerShell && IsCallOperator(line, i)));
                if (separator)
                {
                    yield return current.ToString();
                    current.Clear();
                    if (i + 1 < line.Length && line[i + 1] == c) i++;
                    continue;
                }
                current.Append(c);
            }
            yield return current.ToString();
        }

        /// <summary>In PowerShell, "&amp;" at the start of a statement is the call operator, not a separator.</summary>
        private static bool IsCallOperator(string line, int index)
        {
            if (index + 1 < line.Length && line[index + 1] == '&') return false;
            for (int j = index - 1; j >= 0; j--)
            {
                if (char.IsWhiteSpace(line[j])) continue;
                return line[j] == ';' || line[j] == '|' || line[j] == '{' || line[j] == '(';
            }
            return true;
        }

        /// <summary>Splits a command line on whitespace, honoring double and single quotes. Quotes are removed.</summary>
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inToken = false;
            char quote = '\0';
            foreach (char c in text ?? string.Empty)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else current.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    inToken = true;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (inToken) tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                    continue;
                }
                current.Append(c);
                inToken = true;
            }
            if (inToken) tokens.Add(current.ToString());
            return tokens;
        }

        private static string JoinTokens(IEnumerable<string> tokens)
        {
            return string.Join(" ", tokens.Select(t => t.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? "\"" + t + "\"" : t));
        }

        /// <summary>Lower-case file name without directory, quotes or ".exe".</summary>
        public static string ProgramName(string program)
        {
            string p = Unquote(program ?? string.Empty).Trim();
            int slash = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            if (slash >= 0) p = p.Substring(slash + 1);
            p = p.ToLowerInvariant();
            if (p.EndsWith(".exe", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 4);
            return p;
        }

        private static string SafeExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return dot > slash ? path.Substring(dot).ToLowerInvariant() : string.Empty;
        }

        private static string Unquote(string s)
        {
            return s.Trim().Trim('"', '\'');
        }

        /// <summary>Decodes a PowerShell -EncodedCommand value (Base64 of UTF-16LE). Returns null when invalid.</summary>
        public static string DecodeEncodedCommand(string value)
        {
            try
            {
                return Encoding.Unicode.GetString(Convert.FromBase64String(value.Trim()));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static PowerAction Max(PowerAction a, PowerAction b)
        {
            return (int)a >= (int)b ? a : b;
        }
    }

    /// <summary>Reads a script referenced by a task action (for the classifier). Never throws.</summary>
    public static class ScriptReader
    {
        public const long MaxBytes = 1024 * 1024;

        public static string TryRead(string path, string workingDirectory)
        {
            try
            {
                string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(workingDirectory))
                    p = Path.Combine(Environment.ExpandEnvironmentVariables(workingDirectory.Trim().Trim('"')), p);
                var file = new FileInfo(p);
                if (!file.Exists || file.Length >= MaxBytes) return null;
                return File.ReadAllText(file.FullName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
