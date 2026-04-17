using log4net;
using MissionPlanner.ArduPilot;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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

        public const string BridgeApiVersion = "0.1.0";
        private const string DefaultPrefix = "http://127.0.0.1:9999/";

        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
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
                _listenerThread.Join(2000);
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
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

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath.TrimEnd('/');

                if (context.Request.HttpMethod != "GET")
                {
                    WriteError(context.Response, 405, "method_not_allowed",
                        "Only GET is supported");
                    return;
                }

                if (path == "" || path == "/status")
                {
                    HandleStatus(context.Response);
                }
                else if (path == "/params")
                {
                    HandleParams(context.Response);
                }
                else if (path.StartsWith("/params/") && path.Length > "/params/".Length)
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

            WriteJson(response, 200, new
            {
                vehicle_type = vehicleType,
                name = param.Name,
                value = param.Value,
                type = param.Type.ToString()
            });
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
