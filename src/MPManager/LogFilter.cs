using TaleWorlds.Library;

namespace MPManager;

// Wraps the game's debug manager to drop routine lobby traffic from the log, and copies everything else to
// ServerLog. Every Debug.Print goes through Debug.DebugManager; other calls are passed through unchanged.
//
// A healthy keepalive (every 2 s) prints four lines: "Http Post Request to ...", "Protocol version used for post
// request to ...", and "ClientRestSessionTask::SetFinishedAsSuccessful::...AliveMessage" with and without " done".
// The first two are printed for every lobby request, so they are dropped for all of them; retries and failures
// print other lines and stay visible.
internal sealed class LogFilter : IDebugManager
{
    private readonly IDebugManager _inner;

    private LogFilter(IDebugManager inner)
    {
        _inner = inner;
    }

    public static void Install()
    {
        if (Debug.DebugManager != null && !(Debug.DebugManager is LogFilter))
        {
            Debug.DebugManager = new LogFilter(Debug.DebugManager);
        }
    }

    private static bool IsNoise(string message)
    {
        return message != null
            && (message.StartsWith("Http Post Request to ")
                || message.StartsWith("Protocol version used for post request to ")
                || message.StartsWith("ClientRestSessionTask::SetFinishedAsSuccessful::TaleWorlds.Diamond.Rest.AliveMessage"));
    }

    public void Print(string message, int logLevel, Debug.DebugColor color, ulong debugFilter)
    {
        if (!IsNoise(message))
        {
            ServerLog.Write(message);
            _inner.Print(message, logLevel, color, debugFilter);
        }
    }

    public void ShowWarning(string message) => _inner.ShowWarning(message);

    public void Assert(bool condition, string message, string callerFile, string callerMethod, int callerLine) =>
        _inner.Assert(condition, message, callerFile, callerMethod, callerLine);

    public void SilentAssert(bool condition, string message, bool getDump, string callerFile, string callerMethod, int callerLine) =>
        _inner.SilentAssert(condition, message, getDump, callerFile, callerMethod, callerLine);

    public void PrintError(string error, string stackTrace, ulong debugFilter)
    {
        ServerLog.Write(error);
        _inner.PrintError(error, stackTrace, debugFilter);
    }

    public void PrintWarning(string warning, ulong debugFilter)
    {
        ServerLog.Write(warning);
        _inner.PrintWarning(warning, debugFilter);
    }

    public void ShowError(string message) => _inner.ShowError(message);

    public void ShowMessageBox(string lpText, string lpCaption, uint uType) => _inner.ShowMessageBox(lpText, lpCaption, uType);

    public void DisplayDebugMessage(string message) => _inner.DisplayDebugMessage(message);

    public void WatchVariable(string name, object value) => _inner.WatchVariable(name, value);

    public void WriteDebugLineOnScreen(string message) => _inner.WriteDebugLineOnScreen(message);

    public void RenderDebugLine(Vec3 position, Vec3 direction, uint color, bool depthCheck, float time) =>
        _inner.RenderDebugLine(position, direction, color, depthCheck, time);

    public void RenderDebugSphere(Vec3 position, float radius, uint color, bool depthCheck, float time) =>
        _inner.RenderDebugSphere(position, radius, color, depthCheck, time);

    public void RenderDebugText3D(Vec3 position, string text, uint color, int screenPosOffsetX, int screenPosOffsetY, float time) =>
        _inner.RenderDebugText3D(position, text, color, screenPosOffsetX, screenPosOffsetY, time);

    public void RenderDebugFrame(MatrixFrame frame, float lineLength, float time) => _inner.RenderDebugFrame(frame, lineLength, time);

    public void RenderDebugText(float screenX, float screenY, string text, uint color, float time) =>
        _inner.RenderDebugText(screenX, screenY, text, color, time);

    public void RenderDebugRectWithColor(float left, float bottom, float right, float top, uint color) =>
        _inner.RenderDebugRectWithColor(left, bottom, right, top, color);

    public Vec3 GetDebugVector() => _inner.GetDebugVector();

    public void SetDebugVector(Vec3 value) => _inner.SetDebugVector(value);

    public void SetCrashReportCustomString(string customString) => _inner.SetCrashReportCustomString(customString);

    public void SetCrashReportCustomStack(string customStack) => _inner.SetCrashReportCustomStack(customStack);

    public void SetTestModeEnabled(bool testModeEnabled) => _inner.SetTestModeEnabled(testModeEnabled);

    public void AbortGame() => _inner.AbortGame();

    public void DoDelayedexit(int returnCode) => _inner.DoDelayedexit(returnCode);

    public void ReportMemoryBookmark(string message) => _inner.ReportMemoryBookmark(message);
}
