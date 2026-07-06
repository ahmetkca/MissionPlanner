using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MissionPlanner.McpBridge
{
    public class McpBridgeServer : IDisposable
    {
        private static readonly ILog log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string BridgeApiVersion = "0.2.0";
        private const string DefaultPrefix = "http://127.0.0.1:9999/";

        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly ManualResetEvent _shutdownEvent = new ManualResetEvent(false);
        private readonly string _prefix;

        private static readonly Dictionary<Firmwares, string> VehicleTypeMap =
            new Dictionary<Firmwares, string>
            {
                { Firmwares.ArduPlane, "ArduPlane" },
                { Firmwares.ArduCopter2, "ArduCopter" },
                { Firmwares.ArduRover, "Rover" },
                { Firmwares.ArduSub, "ArduSub" },
                { Firmwares.ArduTracker, "AntennaTracker" },
            };

        public McpBridgeServer(string prefix = DefaultPrefix)
        {
            _prefix = prefix;
        }

        public void Start()
        {
            if (_running) return;

            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();

            _running = true;
            _listenerThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "MCP Bridge listener"
            };
            _listenerThread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            if (_listenerThread != null && _listenerThread != Thread.CurrentThread)
            {
                while (!_shutdownEvent.WaitOne(100))
                    Application.DoEvents();
                _listenerThread.Join();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            try
            {
                while (_running)
                {
                    try
                    {
                        var context = _listener.GetContext();
                        HandleRequest(context);
                    }
                    catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                    {
                        break; // listener stopped — normal shutdown on Windows
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // listener disposed — normal shutdown
                    }
                    catch (Exception ex)
                    {
                        log.Error("MCP bridge accept loop error", ex);
                        if (_running) Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                _shutdownEvent.Set();
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath.TrimEnd('/');
                string method = context.Request.HttpMethod;
                bool isParamByName = path.StartsWith("/params/") && path.Length > "/params/".Length;

                if (method == "GET")
                {
                    if (path == "" || path == "/status")
                    {
                        HandleStatus(context.Response);
                    }
                    else if (path == "/params")
                    {
                        HandleParams(context.Response);
                    }
                    else if (isParamByName)
                    {
                        string paramName = Uri.UnescapeDataString(
                            path.Substring("/params/".Length));
                        HandleParamByName(context.Response, paramName);
                    }
                    else
                    {
                        WriteError(context.Response, 404, "not_found",
                            "Unknown endpoint: " + path);
                    }
                }
                else if (method == "POST" && isParamByName)
                {
                    string paramName = Uri.UnescapeDataString(
                        path.Substring("/params/".Length));
                    HandlePostParam(context.Request, context.Response, paramName);
                }
                else
                {
                    WriteError(context.Response, 405, "method_not_allowed",
                        "Method " + method + " is not supported on " + path);
                }
            }
            catch (Exception ex)
            {
                log.Error("MCP bridge request handler error", ex);
                try
                {
                    WriteError(context.Response, 500, "internal_error", ex.Message);
                }
                catch { }
            }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
                try { context.Response.Close(); } catch { }
            }
        }

        // ──────────────────────────────── route handlers ────────────────────────────────

        private void HandleStatus(HttpListenerResponse response)
        {
            // Capture comPort to local — it can be reassigned at any time
            var port = MainV2.comPort;
            if (port == null)
            {
                WriteJson(response, 200, new
                {
                    bridge_api_version = BridgeApiVersion,
                    mission_planner_version = Application.ProductVersion,
                    connected = false,
                    port = (string)null,
                    vehicle_type = (string)null,
                    vehicle_firmware = (string)null,
                    armed = (bool?)null,
                    selected_sysid = (int?)null,
                    selected_compid = (int?)null,
                    params_loaded = 0,
                    params_total = 0
                });
                return;
            }

            var bs = port.BaseStream;
            bool connected = bs != null && bs.IsOpen;

            string portName = null;
            string vehicleType = null;
            string vehicleFirmware = null;
            bool? armed = null;
            int? sysid = null;
            int? compid = null;
            int paramsLoaded = 0;
            int paramsTotal = 0;

            if (connected)
            {
                try
                {
                    portName = bs.PortName;
                }
                catch { }

                sysid = port.sysidcurrent;
                compid = port.compidcurrent;

                var mav = port.MAVlist[port.sysidcurrent, port.compidcurrent];
                if (mav != null)
                {
                    VehicleTypeMap.TryGetValue(mav.cs.firmware, out vehicleType);
                    vehicleFirmware = mav.VersionString;
                    armed = mav.cs.armed;
                    paramsLoaded = mav.param.TotalReceived;
                    paramsTotal = mav.param.TotalReported;
                }
            }

            WriteJson(response, 200, new
            {
                bridge_api_version = BridgeApiVersion,
                mission_planner_version = Application.ProductVersion,
                connected,
                port = portName,
                vehicle_type = vehicleType,
                vehicle_firmware = vehicleFirmware,
                armed,
                selected_sysid = sysid,
                selected_compid = compid,
                params_loaded = paramsLoaded,
                params_total = paramsTotal
            });
        }

        private void HandleParams(HttpListenerResponse response)
        {
            var port = MainV2.comPort;
            if (port == null || port.BaseStream == null || !port.BaseStream.IsOpen)
            {
                WriteError(response, 503, "not_connected",
                    "Mission Planner is not connected to a MAVLink COM port");
                return;
            }

            var mav = port.MAVlist[port.sysidcurrent, port.compidcurrent];
            if (mav == null)
            {
                WriteError(response, 503, "not_connected",
                    "No vehicle selected");
                return;
            }

            string vehicleType = null;
            VehicleTypeMap.TryGetValue(mav.cs.firmware, out vehicleType);

            var snapshot = mav.param.Snapshot();
            var paramsList = new List<object>(snapshot.Length);
            foreach (var p in snapshot)
            {
                paramsList.Add(new
                {
                    name = p.Name,
                    value = p.Value,
                    type = p.Type.ToString()
                });
            }

            WriteJson(response, 200, new
            {
                vehicle_type = vehicleType,
                params_loaded = mav.param.TotalReceived,
                params_total = mav.param.TotalReported,
                @params = paramsList
            });
        }

        private void HandleParamByName(HttpListenerResponse response, string paramName)
        {
            var port = MainV2.comPort;
            if (port == null || port.BaseStream == null || !port.BaseStream.IsOpen)
            {
                WriteError(response, 503, "not_connected",
                    "Mission Planner is not connected to a MAVLink COM port");
                return;
            }

            var mav = port.MAVlist[port.sysidcurrent, port.compidcurrent];
            if (mav == null)
            {
                WriteError(response, 503, "not_connected",
                    "No vehicle selected");
                return;
            }

            // String indexer is already lock-protected
            var param = mav.param[paramName];
            if (param == null)
            {
                WriteError(response, 404, "not_found",
                    "Parameter '" + paramName + "' is not known to the connected vehicle");
                return;
            }

            string vehicleType = null;
            VehicleTypeMap.TryGetValue(mav.cs.firmware, out vehicleType);

            // Fetch metadata from MP's pdef cache
            string displayName = null;
            string description = null;
            string units = null;
            string unitText = null;
            object range = null;
            Dictionary<string, string> values = null;
            string increment = null;
            string user = null;
            Dictionary<string, string> bitmask = null;
            string rebootRequired = null;
            string readOnly = null;
            string @volatile = null;
            string calibration = null;
            bool metadataAvailable = false;

            if (vehicleType != null)
            {
                displayName = GetMeta(paramName, ParameterMetaDataConstants.DisplayName, vehicleType);
                description = GetMeta(paramName, ParameterMetaDataConstants.Description, vehicleType);
                units = GetMeta(paramName, ParameterMetaDataConstants.Units, vehicleType);
                unitText = GetMeta(paramName, "UnitText", vehicleType);
                increment = GetMeta(paramName, ParameterMetaDataConstants.Increment, vehicleType);
                user = GetMeta(paramName, ParameterMetaDataConstants.User, vehicleType);
                rebootRequired = GetMeta(paramName, ParameterMetaDataConstants.RebootRequired, vehicleType);
                readOnly = GetMeta(paramName, ParameterMetaDataConstants.ReadOnly, vehicleType);
                @volatile = GetMeta(paramName, "Volatile", vehicleType);
                calibration = GetMeta(paramName, "Calibration", vehicleType);

                range = ParseRange(
                    GetMeta(paramName, ParameterMetaDataConstants.Range, vehicleType));
                values = ParseKeyValuePairs(
                    GetMeta(paramName, ParameterMetaDataConstants.Values, vehicleType));
                bitmask = ParseKeyValuePairs(
                    GetMeta(paramName, ParameterMetaDataConstants.Bitmask, vehicleType));

                metadataAvailable = displayName != null || description != null ||
                    units != null || unitText != null || range != null ||
                    values != null || increment != null || user != null ||
                    bitmask != null || rebootRequired != null || readOnly != null ||
                    @volatile != null || calibration != null;
            }

            WriteJson(response, 200, new
            {
                vehicle_type = vehicleType,
                name = param.Name,
                value = param.Value,
                type = param.Type.ToString(),
                metadata_available = metadataAvailable,
                display_name = displayName,
                description,
                units,
                unit_text = unitText,
                range,
                values,
                increment,
                user,
                bitmask,
                reboot_required = rebootRequired,
                read_only = readOnly,
                @volatile,
                calibration
            });
        }

        private class SetParamRequestBody
        {
            public double? value;
            public double? expected_current_value;
        }

        private void HandlePostParam(HttpListenerRequest request, HttpListenerResponse response,
            string paramName)
        {
            var port = MainV2.comPort;
            if (port == null || port.BaseStream == null || !port.BaseStream.IsOpen)
            {
                WriteError(response, 503, "not_connected",
                    "Mission Planner is not connected to a MAVLink COM port");
                return;
            }

            var mav = port.MAVlist[port.sysidcurrent, port.compidcurrent];
            if (mav == null)
            {
                WriteError(response, 503, "not_connected",
                    "No vehicle selected");
                return;
            }

            // String indexer is already lock-protected
            var param = mav.param[paramName];
            if (param == null)
            {
                WriteError(response, 404, "not_found",
                    "Parameter '" + paramName + "' is not known to the connected vehicle");
                return;
            }

            string vehicleType = null;
            VehicleTypeMap.TryGetValue(mav.cs.firmware, out vehicleType);

            // Only refuse the write when pdef explicitly says read_only. If the vehicle type
            // can't be resolved there's no metadata signal to check, so we fail open rather
            // than block a write on a fact we have no way to evaluate.
            if (vehicleType != null)
            {
                string readOnly = GetMeta(paramName, ParameterMetaDataConstants.ReadOnly, vehicleType);
                if (readOnly != null)
                {
                    WriteError(response, 403, "read_only",
                        "Parameter '" + paramName + "' is marked read-only and cannot be written");
                    return;
                }
            }

            SetParamRequestBody body;
            try
            {
                string raw;
                using (var reader = new StreamReader(request.InputStream,
                    request.ContentEncoding ?? Encoding.UTF8))
                {
                    raw = reader.ReadToEnd();
                }

                body = JsonConvert.DeserializeObject<SetParamRequestBody>(raw);
            }
            catch (Exception)
            {
                WriteError(response, 400, "bad_request", "Request body must be valid JSON");
                return;
            }

            if (body == null || !body.value.HasValue || !body.expected_current_value.HasValue)
            {
                WriteError(response, 400, "bad_request",
                    "Body must include numeric 'value' and 'expected_current_value'");
                return;
            }

            double requestedValue = body.value.Value;
            double expectedCurrentValue = body.expected_current_value.Value;
            double liveValueBeforeWrite = param.Value;

            if (!ValuesApproximatelyEqual(expectedCurrentValue, liveValueBeforeWrite))
            {
                WriteJson(response, 409, new
                {
                    error = "value_mismatch",
                    message = "expected_current_value does not match the live value — " +
                        "re-fetch with get_param before retrying",
                    current_value = liveValueBeforeWrite
                });
                return;
            }

            try
            {
                port.setParam(paramName, requestedValue);
            }
            catch (TimeoutException)
            {
                WriteError(response, 504, "timeout",
                    "Timed out waiting for the vehicle to acknowledge the parameter write");
                return;
            }

            // setParamAsync replaces the MAVLinkParamList entry in-place on ack; re-index to
            // read the value the vehicle actually confirmed (may differ if it was clamped).
            var updatedParam = mav.param[paramName];
            double appliedValue = updatedParam != null ? updatedParam.Value : requestedValue;
            bool changed = !ValuesApproximatelyEqual(appliedValue, liveValueBeforeWrite);

            bool rebootRequired = false;
            if (vehicleType != null)
            {
                string rebootRequiredRaw =
                    GetMeta(paramName, ParameterMetaDataConstants.RebootRequired, vehicleType);
                rebootRequired = string.Equals(rebootRequiredRaw, "True",
                    StringComparison.OrdinalIgnoreCase);
            }

            WriteJson(response, 200, new
            {
                name = paramName,
                requested_value = requestedValue,
                applied_value = appliedValue,
                changed,
                reboot_required = rebootRequired
            });
        }

        // ──────────────────────────────── metadata helpers ────────────────────────────────

        // MAVLink REAL32 params round-trip through float32 (see MAVLinkParam.GetValue's
        // 7-significant-digit rounding), so exact double equality is unsafe here.
        private static bool ValuesApproximatelyEqual(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            double diff = Math.Abs(a - b);
            if (diff < 1e-6) return true;
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return diff <= scale * 1e-6;
        }

        private static string GetMeta(string paramName, string metaKey, string vehicleType)
        {
            string raw = ParameterMetaDataRepositoryAPMpdef
                .GetParameterMetaData(paramName, metaKey, vehicleType);
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        private static object ParseRange(string raw)
        {
            if (raw == null) return null;

            string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double min) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double max))
            {
                return new { min, max };
            }

            return null;
        }

        private static Dictionary<string, string> ParseKeyValuePairs(string raw)
        {
            if (raw == null) return null;

            var dict = new Dictionary<string, string>();
            string[] entries = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                int colonIdx = entry.IndexOf(':');
                if (colonIdx > 0 && colonIdx < entry.Length - 1)
                {
                    string key = entry.Substring(0, colonIdx).Trim();
                    string val = entry.Substring(colonIdx + 1).Trim();
                    dict[key] = val;
                }
            }

            return dict.Count > 0 ? dict : null;
        }

        // ──────────────────────────────── response helpers ────────────────────────────────

        private static void WriteJson(HttpListenerResponse response, int statusCode,
            object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";

            string json = JsonConvert.SerializeObject(data, Formatting.None,
                new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Include
                });
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Flush();
        }

        private static void WriteError(HttpListenerResponse response, int statusCode,
            string errorCode, string message)
        {
            WriteJson(response, statusCode, new
            {
                error = errorCode,
                message
            });
        }
    }
}
