# HttpListener Feasibility Research for MCP Bridge on Windows 11 / .NET Framework 4.7.2

**Researcher:** Claude Code Agent  
**Date:** 2026-04-15  
**Context:** In-process HTTP JSON server for ArduPilot Mission Planner, exposing read-only endpoints on `http://127.0.0.1:9999/`

---

## Executive Summary

HttpListener is **feasible and suitable** for the MCP bridge use case on Windows 11 with .NET Framework 4.7.2. The critical requirement — no admin elevation for listening on localhost — is achievable with proper implementation. The blocking `GetContext()` loop on a dedicated background thread is the recommended pattern for simplicity and safety in a WinForms host.

---

## 1. Admin / URL ACL Requirements

### Finding: Localhost Does NOT Require netsh ACL for Basic Scenarios

**Status:** ✅ **Safe for non-admin users**

When listening on `http://127.0.0.1:9999/` (localhost), HttpListener **does not inherently require admin elevation** or `netsh http add urlacl` configuration in most Windows 11 environments. This is the critical finding — MP users will not need elevated privileges just to use the bridge.

### Key Nuances

- **System-Dependent:** Some Windows 11 machines may have 127.0.0.1 properly configured in the http.sys iplisten list by default; others may require explicit netsh configuration (`netsh http add iplisten 127.0.0.1`).
- **Wildcard vs. Specific Addresses:** Listening on `0.0.0.0` or `+` (all interfaces) **does** require admin or explicit ACL via `netsh http add urlacl url=http://+:9999/ user=DOMAIN\user`.
- **Loopback Exception:** Windows has a loopback exception that allows non-admin processes to listen on localhost/127.0.0.1 in many cases, but this is not guaranteed across all configurations.

### Recommendation for MCP Bridge

**Use `http://127.0.0.1:9999/` as the only binding.** If users encounter "Access Denied" on a fresh Windows 11 install, provide optional documentation:
```bash
# Optional fix if Access Denied occurs (requires admin once)
netsh http add iplisten 127.0.0.1
```

**References:**
- [httplistener access denied permanently fixed and created a hole in Windows security | Microsoft Learn](https://learn.microsoft.com/en-us/archive/msdn-technet-forums/5431666b-3ec0-463e-bf57-88d4083b4950)
- [Don Raman's Blog - 127.0.0.1 vs localhost binding issues](https://blogs.iis.net/donraman/can-browse-my-site-using-http-127-0-0-1-http-lt-machine-ip-address-gt-but-cannot-browse-the-same-site-using-http-localhost)

---

## 2. Accept Loop Pattern on .NET Framework 4.7.2

### Finding: Three Viable Patterns; Blocking Loop Recommended for MP

**Status:** ✅ **GetContextAsync() available; blocking GetContext() preferred for safety**

All three patterns are technically available on .NET Framework 4.7.2, but they have different implications for a WinForms host:

### Pattern Comparison

| Pattern | Availability | Pros | Cons | Recommendation |
|---------|--------------|------|------|-----------------|
| **GetContext() blocking loop** | ✅ All versions | Simple, predictable, one thread per listener, easy to interrupt | Blocks thread until request arrives | **RECOMMENDED** |
| **BeginGetContext() async callbacks** | ✅ All versions | Async pattern, no dedicated thread blocked | Callback hell, complex state management | ⚠️ Use if concurrent requests critical |
| **GetContextAsync() + async/await** | ✅ .NET 4.7.1+ | Modern, clean async syntax, fires and forgets | Requires careful task lifecycle management | ⚠️ Use for high-throughput scenarios |

### Recommended Pattern: Blocking GetContext() on Dedicated Thread

For Mission Planner's use case:
- **Low request volume:** 3 endpoints, local-only traffic, typical usage is infrequent
- **Simplicity requirement:** Keep host-critical code simple; no crashes
- **WinForms context:** Dedicated background thread is safe (no UI access, no dependency on UI thread lifecycle)

**Pattern:**
```csharp
// Pseudo-code structure
private Thread _listenerThread;
private bool _running = false;

public void Start()
{
    _running = true;
    _listenerThread = new Thread(AcceptLoop) { IsBackground = true };
    _listenerThread.Start();
}

private void AcceptLoop()
{
    while (_running)
    {
        try
        {
            HttpListenerContext context = _listener.GetContext();
            // Handle request synchronously or fire off to thread pool
            HandleRequest(context);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 995)
        {
            // GetContext() was interrupted by Stop()
            break;
        }
        catch (Exception ex)
        {
            // Log, never throw — host must stay alive
        }
    }
}
```

**Why this is safe:**
- Thread blocks on `GetContext()`, consuming minimal resources
- No recursion, no callbacks, no state machines
- `HttpListenerException` with code 995 signals clean shutdown
- Exception handling prevents any crash from propagating to MP

### GetContextAsync() Consideration

Available in .NET 4.7.1+, but adds complexity:
- Requires `ConfigureAwait(false)` to avoid UI thread marshaling
- Task completion must be handled to avoid dangling work
- Requires more careful error handling for the background fire-and-forget pattern

**Verdict:** GetContextAsync() is overkill for 3 low-traffic endpoints. Use it only if concurrent request handling becomes a bottleneck (unlikely).

**References:**
- [HttpListener.GetContextAsync Method | Microsoft Learn](https://learn.microsoft.com/vi-vn/dotnet/api/system.net.httplistener.getcontextasync?view=netframework-4.7.1)
- [HttpListener.GetContext Method | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener.getcontext?view=net-9.0)
- [Handling Multiple Requests with C# HttpListener Best Practices](https://copyprogramming.com/howto/handling-multiple-requests-with-c-httplistener)
- [How to use HttpListener in Windows Forms C# app | Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/200273/how-to-use-http-listner-in-windows-forums-c-app)

---

## 3. Known Gotchas and Mitigations

### Gotcha #1: HttpListener.Stop() Hangs on Blocking GetContext()

**Issue:** On Windows, calling `Stop()` while a thread is blocked in `GetContext()` does not unblock the thread. The listener remains partially active.

**Evidence:** Documented in dotnet/runtime issues #35526 and #25497. The behavior differs by platform: Windows hangs, Ubuntu throws `HttpListenerException`.

**Mitigation:**
Use a **coordinated shutdown pattern:**
1. Set a flag (`_running = false`)
2. Call `listener.Stop()`
3. Close/dispose the listener
4. The blocked thread will receive `HttpListenerException` (code 995 on Windows when listener is closed during GetContext)
5. Catch and exit the loop cleanly

```csharp
public void Stop()
{
    _running = false;
    try { _listener.Stop(); }
    catch { }
    try { _listener.Close(); }
    catch { }
    
    // Give the thread time to exit gracefully
    _listenerThread?.Join(timeout: 2000);
}
```

**Why this works:** Closing the listener (not just stopping) will cause `GetContext()` to throw, allowing the thread to check `_running` and exit.

### Gotcha #2: Thread Pool Starvation Under Load

**Issue:** If request handlers block on I/O or locks without using async, the thread pool can starve, causing delays.

**Evidence:** Documented in dotnet/runtime #101022. Memory can also grow under sustained load.

**Mitigation for MCP Bridge:**
- Keep request handlers **short and fast** — just read parameters, serialize JSON, write response
- Avoid I/O, database calls, or blocking locks in the handler
- If parameter reads require locks, minimize lock duration
- No synchronous `.Result` or `.Wait()` calls on async operations
- All handlers are read-only (no writes), so contention is minimal

### Gotcha #3: Port Not Released After Stop()

**Issue:** `Stop()` prevents new incoming connections but does not release the port immediately.

**Mitigation:**
- Use `Close()` in addition to `Stop()` for full cleanup
- Implement `IDisposable` to call `Close()` when the bridge server is disposed
- Consider using `listener.Abort()` as a last resort (discards pending requests)

### Gotcha #4: Prefix Already in Use

**Issue:** If port 9999 is already bound, `Start()` will throw `HttpListenerException` (code 183: "Address already in use").

**Mitigation:**
- Wrap `Start()` in try-catch, log the error, and fail gracefully
- Consider an alternate port or error message to the user
- Never crash the host process

**References:**
- [HttpListener Stop() does not properly clean up | websocket-sharp #235](https://github.com/sta/websocket-sharp/issues/235)
- [[HttpListener] No way to stop HttpListener from GetContext | dotnet/runtime #35526](https://github.com/dotnet/runtime/issues/35526)
- [[HttpListener] GetContext method call should unblock after call to Stop | dotnet/runtime #25497](https://github.com/dotnet/runtime/issues/25497)
- [Memory leak and thread pools not closing | onnxruntime #4093](https://github.com/microsoft/onnxruntime/issues/4093)

---

## 4. Concrete Implementation Sketch

### Class Structure: McpBridgeServer.cs

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace MissionPlanner.GCS
{
    /// <summary>
    /// In-process HTTP server for the MCP bridge.
    /// Exposes read-only endpoints on http://127.0.0.1:9999/
    /// 
    /// Runs on a dedicated background thread. Never touches WinForms UI.
    /// Thread-safe to call Start/Stop from any thread, including UI thread.
    /// </summary>
    public class McpBridgeServer : IDisposable
    {
        private const string DefaultPrefix = "http://127.0.0.1:9999/";
        
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running = false;
        private readonly object _lock = new object();

        public McpBridgeServer()
        {
            _listener = new HttpListener();
        }

        /// <summary>
        /// Start the HTTP server. Safe to call from any thread.
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_running)
                    return; // Already running

                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(DefaultPrefix);
                    _listener.Start();
                    
                    _running = true;
                    _listenerThread = new Thread(AcceptLoop)
                    {
                        IsBackground = true,
                        Name = "McpBridgeListener"
                    };
                    _listenerThread.Start();
                }
                catch (HttpListenerException ex)
                {
                    _running = false;
                    throw new InvalidOperationException(
                        $"Failed to start MCP bridge on {DefaultPrefix}. " +
                        $"Port may already be in use. Details: {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    _running = false;
                    throw new InvalidOperationException(
                        $"Failed to start MCP bridge: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Stop the HTTP server. Safe to call from any thread.
        /// Blocks briefly waiting for the listener thread to exit.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_running)
                    return; // Already stopped

                _running = false;

                try
                {
                    _listener?.Stop();
                }
                catch { /* Ignore errors during stop */ }

                try
                {
                    _listener?.Close();
                }
                catch { /* Ignore errors during close */ }

                // Wait briefly for the listener thread to notice _running = false and exit
                if (_listenerThread != null && _listenerThread != Thread.CurrentThread)
                {
                    _listenerThread.Join(timeout: 2000);
                }
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    HandleRequest(context);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    // GetContext() was interrupted because listener was stopped.
                    // This is the expected shutdown path on Windows.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed during GetContext(). Normal shutdown.
                    break;
                }
                catch (Exception ex)
                {
                    // Unexpected error. Log it, but never crash the host.
                    Log($"[MCP Bridge] Unexpected error in accept loop: {ex.Message}");
                    
                    // Avoid tight loop on persistent errors
                    if (_running)
                        Thread.Sleep(100);
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath;
                HttpListenerResponse response = context.Response;

                // Route dispatch
                if (path == "/" || path == "/status")
                {
                    HandleStatus(response);
                }
                else if (path == "/params")
                {
                    HandleParams(response);
                }
                else if (path.StartsWith("/params/"))
                {
                    string paramName = path.Substring("/params/".Length);
                    HandleParamLookup(response, paramName);
                }
                else
                {
                    WriteResponse(response, 404, new { error = "Not found" });
                }
            }
            catch (Exception ex)
            {
                // If anything goes wrong, try to send a 500 error.
                // If that fails, we still don't crash the host.
                try
                {
                    WriteResponse(context.Response, 500, 
                        new { error = "Internal server error", details = ex.Message });
                }
                catch
                {
                    // Response send failed. Just close and move on.
                }
            }
            finally
            {
                // Always close the response
                try { context.Response.OutputStream.Close(); }
                catch { }
                try { context.Response.Close(); }
                catch { }
            }
        }

        private void HandleStatus(HttpListenerResponse response)
        {
            WriteResponse(response, 200, new { status = "ok" });
        }

        private void HandleParams(HttpListenerResponse response)
        {
            try
            {
                // Read parameters from Mission Planner's param store.
                // This is read-only and should not touch UI.
                // Example: var allParams = ComPort.MAV.param.ToJObject();
                
                var allParams = new Dictionary<string, object>
                {
                    // Placeholder: in real implementation, call ComPort.MAV.param
                    { "example_param", 123.456 }
                };

                WriteResponse(response, 200, allParams);
            }
            catch (Exception ex)
            {
                WriteResponse(response, 500, 
                    new { error = "Failed to read parameters", details = ex.Message });
            }
        }

        private void HandleParamLookup(HttpListenerResponse response, string paramName)
        {
            try
            {
                // Look up a single parameter by name.
                // Example: var value = ComPort.MAV.param[paramName];
                
                var value = 42.0; // Placeholder
                WriteResponse(response, 200, new { name = paramName, value = value });
            }
            catch (KeyNotFoundException)
            {
                WriteResponse(response, 404, 
                    new { error = $"Parameter '{paramName}' not found" });
            }
            catch (Exception ex)
            {
                WriteResponse(response, 500, 
                    new { error = "Failed to read parameter", details = ex.Message });
            }
        }

        /// <summary>
        /// Helper: write a JSON response with the given status code.
        /// </summary>
        private void WriteResponse(HttpListenerResponse response, int statusCode, object data)
        {
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = "application/json";

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);

                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Flush();
            }
            catch (Exception ex)
            {
                // If serialization or sending fails, log it.
                // Caller's finally block will close the response.
                Log($"[MCP Bridge] Failed to write response: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            // Route to Mission Planner's logging system
            // For now, a placeholder
            Console.WriteLine(message);
        }

        public void Dispose()
        {
            Stop();
            _listener?.Dispose();
        }
    }
}
```

### Integration into MainV2

```csharp
public partial class MainV2 : Form
{
    private McpBridgeServer _mcpBridge;

    private void OnLoad(object sender, EventArgs e)
    {
        // ... existing code ...

        // Start MCP bridge after comPort is ready
        if (comPort != null && comPort.IsOpen)
        {
            try
            {
                _mcpBridge = new McpBridgeServer();
                _mcpBridge.Start();
                Log.Info("MCP bridge started on http://127.0.0.1:9999/");
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP bridge failed to start: {ex.Message}");
                // Do not crash; MP continues without bridge
            }
        }
    }

    private void MainV2_FormClosing(object sender, FormClosingEventArgs e)
    {
        // ... existing code ...

        _mcpBridge?.Stop();
        _mcpBridge?.Dispose();
    }
}
```

### Error Handling Philosophy

The sketch follows this principle: **Never crash the host.** All errors are caught and logged:
- `AcceptLoop()` catches all exceptions, logs, and continues
- `HandleRequest()` catches all exceptions and sends HTTP 500
- `WriteResponse()` catches serialization/send errors and logs
- Finalizers ensure response streams are always closed

This ensures that a malformed request or unexpected parameter read error does not bring down Mission Planner.

---

## Summary & Recommendations

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Binding** | `http://127.0.0.1:9999/` (localhost only) | No admin elevation required for users in typical Windows 11 configs |
| **Accept Loop** | Blocking `GetContext()` on dedicated thread | Simple, predictable, safe for low-traffic local-only endpoints |
| **Async Pattern** | No — blocking is fine | Overhead of GetContextAsync/BeginGetContext not justified for 3 endpoints |
| **Thread Pool** | Not a concern | Handlers are short, synchronous, read-only — no starvation risk |
| **Shutdown** | Coordinated stop: flag + Stop() + Close() | Safely unblocks GetContext() and releases resources |
| **Error Handling** | Try-catch everywhere; never throw | Mission Planner stability is non-negotiable |
| **JSON Library** | Newtonsoft.Json 13.0.3 (already available) | No new dependencies required |

**Next Steps:**
1. Implement McpBridgeServer.cs with the above sketch as a template
2. Integrate into MainV2.OnLoad and MainV2_FormClosing
3. Implement parameter read handlers to pull from ComPort.MAV.param (thread-safe access audit required — see task #3)
4. Test with manual HTTP requests and verify no crashes under edge cases (port in use, malformed JSON, param not found, etc.)

---

## References

### Admin / ACL Behavior
- [httplistener access denied permanently fixed | Microsoft Learn](https://learn.microsoft.com/en-us/archive/msdn-technet-forums/5431666b-3ec0-463e-bf57-88d4083b4950)
- [Why does HttpListener require admin rights | SolutionFall.Com](https://solutionfall.com/question/why-does-httplistener-require-admin-rights-or-running-netsh-as-admin-while-aspnet-doesnt/)
- [Don Raman's Blog - localhost vs 127.0.0.1 binding](https://blogs.iis.net/donraman/can-browse-my-site-using-http-127-0-0-1-http-lt-machine-ip-address-gt-but-cannot-browse-the-same-site-using-http-localhost)

### Accept Loop Patterns
- [HttpListener.GetContext Method | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener.getcontext?view=net-9.0)
- [HttpListener.GetContextAsync Method | Microsoft Learn](https://learn.microsoft.com/vi-vn/dotnet/api/system.net.httplistener.getcontextasync?view=netframework-4.7.1)
- [Handling Multiple Requests with C# HttpListener](https://copyprogramming.com/howto/handling-multiple-requests-with-c-httplistener)
- [How to use HttpListener in Windows Forms C# app | Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/200273/how-to-use-http-listner-in-windows-forums-c-app)

### Known Gotchas
- [[HttpListener] No way to stop HttpListener from GetContext | dotnet/runtime #35526](https://github.com/dotnet/runtime/issues/35526)
- [[HttpListener] GetContext should unblock after Stop | dotnet/runtime #25497](https://github.com/dotnet/runtime/issues/25497)
- [HttpListener.Stop() does not properly clean up | websocket-sharp #235](https://github.com/sta/websocket-sharp/issues/235)
- [HttpListener memory leak | dotnet/runtime #101022](https://github.com/dotnet/runtime/issues/101022)
- [Memory leak in System.Net.HttpListener | dotnet/runtime #27469](https://github.com/dotnet/runtime/issues/27469)

### WinForms Integration
- [How to handle cross-thread operations with controls | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-make-thread-safe-calls)
- [Backgrounding Tasks in a WinForms Application | shendrick.net](https://shendrick.net/Coding%20Tips/2023/06/18/winformsasyncawait.html)
- [Multithreading in WinForms | Visual Studio Magazine](https://visualstudiomagazine.com/articles/2010/11/18/multithreading-in-winforms.aspx)
- [Threading in Windows Forms | Jon Skeet](https://jonskeet.uk/csharp/threads/winforms.html)

### JSON Response Handling
- [HttpListenerResponse Examples | HotExamples](https://csharp.hotexamples.com/examples/System.Net/HttpListenerResponse/-/php-httplistenerresponse-class-examples.html)
- [Build an HTTP Server in .NET — Practical Guide | Medium](https://medium.com/@bhargavkoya56/how-to-build-an-http-server-in-net-practical-guide-716e6e0665e7)
- [Basic HttpListener web service | Gabe's Code](https://www.gabescode.com/dotnet/2018/11/01/basic-HttpListener-web-service.html)
