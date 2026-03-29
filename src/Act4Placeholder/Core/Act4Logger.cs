//=============================================================================
// Act4Logger.cs | Act4Placeholder - Slay the Spire 2 Mod
// EN: Mod-specific logger that filters out common engine noise and exposes named section/info/warn/error helpers used throughout the codebase.
// ZH: Mod专用日志工具，过滤引擎常见噪音警告，提供贯穿整个代码库使用的section/info/warn/error辅助方法。
//=============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Act4Placeholder;

/// <summary>
/// Writes a privacy-safe mod log to Act4_Logs.txt in the STS2 logs directory.
///
/// Contains:
///   - Filtered STS2 startup lines (game version, FMOD, Steam init, atlas loads, mod load)
///   - All Act4Placeholder patch results and status
///   - Main-menu SaveSync / RunSlots patch status
///   - Real game/engine ERRORs and WARNINGs (minus known-harmless noise)
///   - ERROR context: ~10 lines following each ERROR for stack trace / details
///   - Active run info (character, ascension, floor, act) updated periodically
///
/// Log rotation: keeps the 3 most recent sessions:
///   Act4_Logs.txt              (current)
///   Act4_Logs_2GamesAgo.txt    (previous)
///   Act4_Logs_3GamesAgo.txt    (oldest, deleted when a new session starts)
///
/// Deliberately excludes from godot.log:
///   - PATH environment variable
///   - Device model / machine name
///   - Executable and data directory paths (contain username)
///   - Steam profile ID
///   - Processor name, memory sizes, screen details
///   - Graphics adapter version strings
///   - Teardown RID leak reports ("leaked at exit", "never freed") - normal Godot shutdown
///   - "Asset not cached" warnings - very common, not actionable
///   - D3D12 PSO caching warning - always present on Windows, expected
///
/// Share THIS file (not godot.log) when reporting issues.
/// </summary>
internal static class Act4Logger
{
	private const string LogFileName = "Act4_Logs.txt";
	private const string LogFileName2 = "Act4_Logs_2GamesAgo.txt";
	private const string LogFileName3 = "Act4_Logs_3GamesAgo.txt";
	// Also clean up old-named files from before the rename.
	private const string LegacyLogFileName = "Act4Placeholder_Logs.txt";

	private static string? _logPath;
	private static string? _logDir;
	private static readonly object _lock = new();
	private static string? _godotLogPath;
	private static volatile int _godotLogOffsetHi; // high 32 bits of offset
	private static volatile int _godotLogOffsetLo; // low  32 bits of offset

	// Tracks the last run-info line written so we only update when something changes.
	private static string? _lastRunInfoLine;

	private static long GodotLogOffset
	{
		get => ((long)(uint)_godotLogOffsetHi << 32) | (uint)_godotLogOffsetLo;
		set { _godotLogOffsetHi = (int)(value >> 32); _godotLogOffsetLo = (int)(value & 0xFFFFFFFFL); }
	}

	// Lines matching ANY of these patterns are excluded even when they would otherwise pass
	// both the allow-list and PII filter.  Covers teardown / shutdown noise and high-volume
	// informational lines that are never actionable.
	private static readonly Regex _noisePattern = new(
		@"were leaked at exit" +
		@"|RIDs? of type .+ were leaked" +
		@"|shaders? of type .+ were never freed" +
		@"|RID allocations? of type .+ were leaked" +
		@"|Asset not cached:" +          // [WARN] Asset not cached: res://... - very common, not actionable
		@"|PSO caching is not implemented",  // D3D12 driver warning, always present on Windows
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	// Substrings that make a godot.log line eligible for inclusion.
	// Order doesn't matter – any single match admits the line (subject to the PII filter and noise filter below).
	private static readonly string[] _allowedSubstrings =
	{
		"MegaDot v",                           // engine version header
		"FMOD Sound System:",                  // audio init
		"[Sentry.NET] Initialized:",           // crash-reporting env (no user info)
		"Steamworks:",                         // Steam SDK init
		"Steam is running:",                   // Steam running bool
		"Steam is enabled,",                   // Steam save mode
		"Registered ",                         // "Registered N migrations"
		"Current save versions:",              // schema versions
		"Found mod pck file",                  // mod detected
		"Loading assembly DLL",                // DLL loaded
		"Calling initializer method",          // mod entry point called
		"[Act4Placeholder]",                   // all our Init() logs
		"Finished mod initialization",         // loader confirmation
		"--- RUNNING MODDED!",                 // modded flag
		"Loading locale",                      // locale load (res:// only)
		"Found loc table from mod:",           // our locale merges
		"ModelIdSerializationCache initialized", // model DB ready
		"AtlasManager: Loaded",                // atlas loads
		"Time to main menu",                   // startup timing
		"[Act4Placeholder.",                   // UnifiedSavePath / RunSlots patch logs
		// ── Errors & warnings ───────────────────────────────────────────
		// Real game/engine ERRORs (teardown noise is blocked by _noisePattern later)
		"ERROR:",
		"[ERROR]",
		// Real WARNING lines ("Asset not cached" and D3D12 PSO blocked by _noisePattern)
		"WARNING:",
		"[WARN]",
	};

	// Lines matching ANY of these patterns are excluded even if they pass the allow-list.
	// Covers personal/machine-identifying information.
	private static readonly Regex _piiPattern = new(
		@"[Uu]sers[/\\]" +
		@"|AppData[/\\]" +
		@"|user://steam/\d" +
		@"|PATH:" +
		@"|Device Model:" +
		@"|Executable Path:" +
		@"|Data Directory:" +
		@"|User Data Directory:" +
		@"|Processor Name:" +
		@"|Processor Count:" +
		@"|Command Line" +
		@"|Memory Info:" +
		@"|  physical:" +
		@"|  free:" +
		@"|  available:" +
		@"|[Ss]tatic [Mm]emory" +
		@"|Important Environment" +
		@"|Wrote \d+ bytes to path=user://" +
		@"|76561\d{12}" +           // Steam 64-bit ID pattern
		@"|=== Godot OS" +
		@"|Architecture:" +
		@"|Screen info \(" +
		@"|[Vv]ideo [Mm]emory" +
		@"|Is Debug Build:" +
		@"|Is Sandboxed:" +
		@"|Is Stdout Verbose:" +
		@"|Is Low Processor" +
		@"|Release Commit:" +
		@"|Graphics adapter" +
		@"|Timestamp:" +
		@"|OS Version:" +
		@"|Distribution Name:" +
		@"|Is UserFS Persistent:",
		RegexOptions.Compiled);

	// Replaces absolute filesystem paths like "D:/SteamLibrary/.../file.pck" with just "file.pck".
	// Keeps res:// and user:// virtual paths intact.
	private static readonly Regex _absPathPattern = new(
		@"[A-Za-z]:[/\\][^""'\s\r\n]*[/\\]([^/\\""'\s\r\n]+)",
		RegexOptions.Compiled);

	// Detects lines that are ERROR or [ERROR], used by the context-capture logic.
	private static readonly Regex _errorLinePattern = new(
		@"\bERROR[:\]]",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	private const int ErrorContextLines = 10;

	// ─────────────────────────────────────────────────────────────────────────
	// Public API
	// ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Call once at the very start of ModEntry.Init().
	/// Rotates old logs, writes the log file header, and backfills safe lines from godot.log.
	/// </summary>
	public static void Initialize(string modVersion)
	{
		try
		{
			var logDir = ProjectSettings.GlobalizePath("user://logs");
			if (!Directory.Exists(logDir))
				Directory.CreateDirectory(logDir);

			_logDir = logDir;
			_logPath = Path.Combine(logDir, LogFileName);

			// ── Log rotation: keep 3 most recent sessions ──
			RotateLogs(logDir);

			var sb = new StringBuilder();
			sb.AppendLine("=================================================================");
			sb.AppendLine($"  Act4Placeholder v{modVersion} - Mod Log");
			sb.AppendLine($"  Session: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
			sb.AppendLine("  Share THIS file (not godot.log) when reporting issues.");
			sb.AppendLine("  godot.log contains personal system info, this one does not.");
			sb.AppendLine("=================================================================");
			sb.AppendLine();
			sb.AppendLine("  Run: (waiting for run to start...)");
			sb.AppendLine();

			// Backfill safe startup lines from the current godot.log.
			// By the time Init() runs, godot.log already has everything up to
			// "Calling initializer method of type Act4Placeholder.ModEntry".
			sb.AppendLine("=== STS2 Startup (filtered, no personal info) ===");
			var godotLog = Path.Combine(logDir, "godot.log");
			if (File.Exists(godotLog))
			{
				try
				{
					// FileShare.ReadWrite so we don't block Godot's own logger.
					using var fs = new FileStream(godotLog, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
					using var reader = new StreamReader(fs, Encoding.UTF8);
					string? line;
					while ((line = reader.ReadLine()) != null)
					{
						if (IsSafe(line))
							sb.AppendLine(Sanitize(line));
					}
					// Record where we stopped so the background poller can pick up new lines later.
					GodotLogOffset = fs.Length;
					_godotLogPath = godotLog;
				}
				catch (IOException)
				{
					sb.AppendLine("(Could not read godot.log for backfill, file may be locked)");
				}
			}
			else
			{
				sb.AppendLine("(godot.log not found in expected location)");
			}

			sb.AppendLine();
			sb.AppendLine("=== Mod Init ===");

			File.WriteAllText(_logPath, sb.ToString(), Encoding.UTF8);

			// Start background poller to capture engine ERRORs/WARNINGs written to godot.log
			// during gameplay (e.g. mid-boss-fight crashes that wouldn't be in the backfill).
			_ = Task.Run(PollGodotLogLoopAsync);
		}
		catch (Exception ex)
		{
			// Never crash mod init because logging failed.
			GD.PrintErr($"[Act4Placeholder] Act4Logger.Initialize failed: {ex.Message}");
		}
	}

	public static void Info(string message) => Append($"[{Ts()}] [INFO] {message}");
	public static void Warn(string message) => Append($"[{Ts()}] [WARN] {message}");
	public static void Error(string message) => Append($"[{Ts()}] [ERROR] {message}");

	/// <summary>Writes a blank line followed by a section header.</summary>
	public static void Section(string title) => Append($"\n=== {title} ===");

	/// <summary>
	/// Updates the "Run:" line near the top of the log with current run info,
	/// and appends a timestamped status line in the body. Called when a run is
	/// loaded/resumed and periodically by the poller.
	/// </summary>
	public static void UpdateRunInfo()
	{
		if (_logPath == null) return;
		try
		{
			string runLine = BuildRunInfoLine();
			if (runLine == _lastRunInfoLine) return;
			_lastRunInfoLine = runLine;

			// Update the "Run:" placeholder line near the top of the file.
			UpdateRunInfoHeader(runLine);

			// Also append a timestamped entry in the log body.
			Append($"[{Ts()}] [INFO] Run status: {runLine}");
		}
		catch { /* never crash on run-info update */ }
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Internal helpers
	// ─────────────────────────────────────────────────────────────────────────

	private static void Append(string line)
	{
		if (_logPath == null) return;
		try
		{
			lock (_lock)
				File.AppendAllText(_logPath, line + System.Environment.NewLine, Encoding.UTF8);
		}
		catch { /* never crash on log write */ }
	}

	private static string Ts() => DateTime.Now.ToString("HH:mm:ss");

	private static bool IsSafe(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) return false;
		if (_piiPattern.IsMatch(line)) return false;
		if (_noisePattern.IsMatch(line)) return false;

		foreach (var sub in _allowedSubstrings)
		{
			if (line.Contains(sub, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>Returns true if the line is safe to include, OR if it should be included
	/// as error-context (not itself an allow-listed line, but follows an ERROR).</summary>
	private static bool IsSafeForContext(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) return false;
		if (_piiPattern.IsMatch(line)) return false;
		// Don't apply noise filter for context lines, stack traces may contain
		// substrings that match noise patterns but are still valuable.
		return true;
	}

	private static bool IsErrorLine(string line)
	{
		return _errorLinePattern.IsMatch(line);
	}

	private static string Sanitize(string line)
	{
		// Replace absolute OS paths (C:\...\file) with just the filename.
		// Virtual paths like res:// and user:// are left intact.
		return _absPathPattern.Replace(line, m => m.Groups[1].Value);
	}

	// ── Log rotation ────────────────────────────────────────────────────────

	private static void RotateLogs(string logDir)
	{
		try
		{
			string current = Path.Combine(logDir, LogFileName);
			string prev = Path.Combine(logDir, LogFileName2);
			string oldest = Path.Combine(logDir, LogFileName3);
			string legacy = Path.Combine(logDir, LegacyLogFileName);

			// Delete oldest backup.
			if (File.Exists(oldest))
				File.Delete(oldest);

			// Move previous → oldest.
			if (File.Exists(prev))
				File.Move(prev, oldest);

			// Move current → previous.
			if (File.Exists(current))
				File.Move(current, prev);

			// Clean up old-named log if it exists (one-time migration).
			if (File.Exists(legacy))
			{
				try { File.Delete(legacy); } catch { }
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[Act4Placeholder] Log rotation failed: {ex.Message}");
		}
	}

	// ── Run info header ─────────────────────────────────────────────────────

	private static string BuildRunInfoLine()
	{
		try
		{
			RunManager? rm = RunManager.Instance;
			RunState? runState = rm?.DebugOnlyGetState();
			if (runState == null)
				return "(no active run)";

			var parts = new List<string>();

			// Character name(s)
			IReadOnlyCollection<Player> players = runState.Players;
			if (players != null && players.Count > 0)
			{
				var names = new List<string>();
				foreach (Player p in players)
				{
					string name = p.Character?.Title?.GetFormattedText() ?? "Unknown";
					names.Add(name);
				}
				parts.Add(string.Join(" + ", names));
			}

			// Ascension
			int asc = runState.AscensionLevel;
			if (asc > 0)
				parts.Add($"A{asc}");

			// Act
			int actIdx = runState.CurrentActIndex;
			IReadOnlyList<ActModel> acts = runState.Acts;
			if (acts != null && actIdx >= 0 && actIdx < acts.Count)
			{
				string actTitle = acts[actIdx].Title?.GetFormattedText() ?? $"Act {actIdx + 1}";
				parts.Add(actTitle);
			}

			// Floor (sum of completed act floors + visited coords in current act)
			int floor = 0;
			try
			{
				SerializableRun? ser = rm?.ToSave((MegaCrit.Sts2.Core.Rooms.AbstractRoom?)null);
				if (ser != null)
				{
					for (int i = 0; i < ser.CurrentActIndex && ser.Acts != null && i < ser.Acts.Count; i++)
					{
						ActModel? actModel = ModelDb.GetById<ActModel>(ser.Acts[i].Id);
						if (actModel != null)
							floor += actModel.GetNumberOfFloors(ser.Players?.Count > 1);
					}
					floor += ser.VisitedMapCoords?.Count ?? 0;
					parts.Add($"Floor {floor}");
				}
			}
			catch { /* floor calculation is best-effort */ }

			// Brutal
			if (ModSupport.IsBrutalAct4(runState))
				parts.Add("Brutal");

			// Player count
			if (players != null && players.Count > 1)
				parts.Add($"{players.Count}P co-op");

			return string.Join(" | ", parts);
		}
		catch
		{
			return "(error reading run info)";
		}
	}

	private static void UpdateRunInfoHeader(string runLine)
	{
		if (_logPath == null) return;
		try
		{
			string content;
			lock (_lock)
				content = File.ReadAllText(_logPath, Encoding.UTF8);

			// Find and replace the "  Run: ..." line near the top.
			const string runPrefix = "  Run: ";
			int idx = content.IndexOf(runPrefix, StringComparison.Ordinal);
			if (idx < 0) return;

			int lineEnd = content.IndexOf('\n', idx);
			if (lineEnd < 0) lineEnd = content.Length;
			// Include \r if present.
			int replaceEnd = lineEnd;

			string newLine = $"{runPrefix}{runLine}";
			string updated = content.Substring(0, idx) + newLine + content.Substring(replaceEnd);

			lock (_lock)
				File.WriteAllText(_logPath, updated, Encoding.UTF8);
		}
		catch { /* never crash on header update */ }
	}

	// ── Godot.log polling with ERROR context capture ────────────────────────

	/// <summary>
	/// Reads any new lines appended to godot.log since the last poll and appends
	/// safe/filtered ones to our mod log. When an ERROR line is found, the next
	/// ~10 lines are also included (PII-filtered + sanitized) for stack trace context.
	/// </summary>
	private static void FlushGodotLog()
	{
		if (_godotLogPath == null || _logPath == null) return;
		try
		{
			string output;
			long newOffset;
			using (var fs = new FileStream(_godotLogPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
			{
				newOffset = fs.Length;
				long current = GodotLogOffset;
				if (newOffset <= current) return;
				fs.Seek(current, SeekOrigin.Begin);
				using var reader = new StreamReader(fs, Encoding.UTF8);
				var sb = new StringBuilder();
				int errorContextRemaining = 0;

				string? line;
				while ((line = reader.ReadLine()) != null)
				{
					bool isSafeLine = IsSafe(line);

					if (isSafeLine)
					{
						sb.AppendLine(Sanitize(line));
						// If this line is an ERROR, start capturing context lines.
						if (IsErrorLine(line))
							errorContextRemaining = ErrorContextLines;
					}
					else if (errorContextRemaining > 0)
					{
						// Not normally included, but we're in error-context mode.
						// Still apply PII filtering.
						if (IsSafeForContext(line))
							sb.AppendLine("    " + Sanitize(line));
						errorContextRemaining--;
					}

					// Reset context counter if we hit a new error while already capturing.
					if (errorContextRemaining > 0 && isSafeLine && IsErrorLine(line))
						errorContextRemaining = ErrorContextLines;
				}
				output = sb.ToString();
			}
			lock (_lock)
			{
				if (output.Length > 0)
					File.AppendAllText(_logPath, output, Encoding.UTF8);
				GodotLogOffset = newOffset;
			}
		}
		catch { /* never crash on log poll */ }
	}

	private static async Task PollGodotLogLoopAsync()
	{
		int tickCount = 0;
		while (true)
		{
			await Task.Delay(6000);
			FlushGodotLog();

			// Update run info every ~60 seconds (10 ticks × 6s).
			tickCount++;
			if (tickCount % 10 == 0)
			{
				UpdateRunInfo();
			}
		}
	}
}
