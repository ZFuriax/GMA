using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MusicPlayer
{
    public sealed class AudioDeviceManager : IMMNotificationClient, IDisposable
    {
        private readonly object _gate = new();
        private readonly MMDeviceEnumerator _enumerator = new();

        private string? _lastDefaultRenderDeviceId;
        private DateTime _lastRaiseUtc = DateTime.MinValue;

        public event Action<string>? DefaultRenderDeviceChanged;

        public AudioDeviceManager()
        {
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);

                var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _lastDefaultRenderDeviceId = dev.ID;
            }
            catch
            {
                _lastDefaultRenderDeviceId = null;
            }
        }

        public string? GetCurrentDefaultRenderDeviceId()
        {
            try
            {
                var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return dev.ID;
            }
            catch
            {
                return null;
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia)
                return;

            if (string.IsNullOrWhiteSpace(defaultDeviceId))
                return;

            bool shouldRaise = false;

            lock (_gate)
            {
                // Suppress exact duplicate events.
                if (string.Equals(_lastDefaultRenderDeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                    return;

                // Small debounce window for noisy device-change bursts.
                var now = DateTime.UtcNow;
                if ((now - _lastRaiseUtc).TotalMilliseconds < 250)
                {
                    _lastDefaultRenderDeviceId = defaultDeviceId;
                    _lastRaiseUtc = now;
                    shouldRaise = true;
                }
                else
                {
                    _lastDefaultRenderDeviceId = defaultDeviceId;
                    _lastRaiseUtc = now;
                    shouldRaise = true;
                }
            }

            if (shouldRaise)
                DefaultRenderDeviceChanged?.Invoke(defaultDeviceId);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }
    }
}