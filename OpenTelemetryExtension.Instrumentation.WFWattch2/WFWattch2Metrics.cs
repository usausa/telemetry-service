namespace OpenTelemetryExtension.Instrumentation.WFWattch2;

using System;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;

using DeviceLib.WFWattch2;

internal sealed class WFWattch2Metrics : IDisposable
{
    internal static readonly AssemblyName AssemblyName = typeof(WFWattch2Metrics).Assembly.GetName();
    internal static readonly string MeterName = AssemblyName.Name!;

    private static readonly Meter MeterInstance = new(MeterName, AssemblyName.Version!.ToString());

    private readonly Device[] devices;

    private readonly Timer timer;

    public WFWattch2Metrics(WFWattch2Options options)
    {
        devices = options.Device.Select(static x => new Device(x)).ToArray();

        MeterInstance.CreateObservableUpDownCounter("sensor.power", () => ToMeasurement(static x => x.Power));
        MeterInstance.CreateObservableUpDownCounter("sensor.current", () => ToMeasurement(static x => x.Current));
        MeterInstance.CreateObservableUpDownCounter("sensor.voltage", () => ToMeasurement(static x => x.Voltage));

        timer = new Timer(_ => Update(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(options.Interval));
    }

    public void Dispose()
    {
        timer.Dispose();
        foreach (var device in devices)
        {
            device.Dispose();
        }
    }

    private List<Measurement<double>> ToMeasurement(Func<Device, double?> selector)
    {
        var values = new List<Measurement<double>>(devices.Length);

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var device in devices)
        {
            var value = selector(device);
            if (value.HasValue)
            {
                values.Add(new Measurement<double>(
                    value.Value,
                    new("model", "wfwatch2"),
                    new("address", device.Setting.Address),
                    new("name", device.Setting.Name)));
            }
        }

        return values;
    }

    private void Update()
    {
        foreach (var device in devices)
        {
            _ = Task.Run(async () => await device.UpdateAsync());
        }
    }

    //--------------------------------------------------------------------------------
    // Device
    //--------------------------------------------------------------------------------

    private sealed class Device : IDisposable
    {
        private readonly SemaphoreSlim semaphore = new(1, 1);

        private readonly object sync = new();

        private readonly WattchClient client;

        private double? power;
        private double? voltage;
        private double? current;

        public DeviceEntry Setting { get; }

        public double? Power
        {
            get
            {
                lock (sync)
                {
                    return power;
                }
            }
        }

        public double? Voltage
        {
            get
            {
                lock (sync)
                {
                    return voltage;
                }
            }
        }

        public double? Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public Device(DeviceEntry setting)
        {
            Setting = setting;
            client = new WattchClient(IPAddress.Parse(setting.Address));
        }

        public void Dispose()
        {
            client.Dispose();
            semaphore.Dispose();
        }

#pragma warning disable CA1031
        public async ValueTask UpdateAsync()
        {
            await semaphore.WaitAsync();
            try
            {
                if (!client.IsConnected())
                {
                    await client.ConnectAsync();
                }

                var result = await client.UpdateAsync();
                if (result)
                {
                    ReadValues();
                }
                else
                {
                    ClearValues();
                }
            }
            catch
            {
                ClearValues();

                client.Close();
            }
            finally
            {
                semaphore.Release();
            }
        }
#pragma warning restore CA1031

        private void ReadValues()
        {
            lock (sync)
            {
                power = client.Power;
                voltage = client.Voltage;
                current = client.Current;
            }
        }

        private void ClearValues()
        {
            lock (sync)
            {
                power = null;
                voltage = null;
                current = null;
            }
        }
    }
}