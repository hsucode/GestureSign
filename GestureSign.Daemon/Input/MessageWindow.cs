﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GestureSign.Common.Configuration;
using GestureSign.Common.Input;
using GestureSign.Common.Localization;
using GestureSign.Daemon.Native;
using Microsoft.Win32;

namespace GestureSign.Daemon.Input
{
    public class MessageWindow : NativeWindow
    {
        private bool _xAxisDirection;
        private bool _yAxisDirection;
        private bool _isAxisCorresponds;
        private Screen _currentScr;

        private static readonly HandleRef HwndMessage = new HandleRef(null, new IntPtr(-3));

        private List<RawData> _outputTouchs = new List<RawData>(1);
        private int _requiringContactCount;
        private Dictionary<IntPtr, ushort> _validDevices = new Dictionary<IntPtr, ushort>();
        private Point _physicalMax;

        private Devices _sourceDevice;
        private List<ushort> _registeredDeviceList = new List<ushort>(1);
        private int? _penLastActivity;
        private bool _ignoreTouchInputWhenUsingPen;
        private DeviceStates _penGestureButton;

        public event RawPointsDataMessageEventHandler PointsIntercepted;

        public MessageWindow()
        {
            CreateWindow();
            UpdateRegistration();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        ~MessageWindow()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            DestroyWindow();
        }

        public bool CreateWindow()
        {
            if (Handle == IntPtr.Zero)
            {
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateHandle(new CreateParams
                {
                    Style = 0,
                    ExStyle = WS_EX_NOACTIVATE,
                    ClassStyle = 0,
                    Caption = "GSMessageWindow",
                    Parent = (IntPtr)HwndMessage
                });
            }
            return Handle != IntPtr.Zero;
        }

        public void DestroyWindow()
        {
            DestroyWindow(true, IntPtr.Zero);
        }

        public override void DestroyHandle()
        {
            DestroyWindow(false, IntPtr.Zero);
            base.DestroyHandle();
        }

        protected override void OnHandleChange()
        {
            UpdateRegistration();
            base.OnHandleChange();
        }

        private bool GetInvokeRequired(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            int pid;
            var hwndThread = NativeMethods.GetWindowThreadProcessId(new HandleRef(this, hWnd), out pid);
            var currentThread = NativeMethods.GetCurrentThreadId();
            return (hwndThread != currentThread);
        }

        private void DestroyWindow(bool destroyHwnd, IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                hWnd = Handle;
            }

            if (GetInvokeRequired(hWnd))
            {
                NativeMethods.PostMessage(new HandleRef(this, hWnd), NativeMethods.WmClose, 0, 0);
                return;
            }

            lock (this)
            {
                if (destroyHwnd)
                {
                    base.DestroyHandle();
                }
            }
        }

        public void UpdateRegistration()
        {
            _ignoreTouchInputWhenUsingPen = AppConfig.IgnoreTouchInputWhenUsingPen;
            var penSetting = AppConfig.PenGestureButton;
            _penGestureButton = penSetting & (DeviceStates.Invert | DeviceStates.RightClickButton);

            _validDevices.Clear();

            UpdateRegisterState(true, NativeMethods.TouchScreenUsage);
            UpdateRegisterState(_ignoreTouchInputWhenUsingPen || _penGestureButton != 0 && (penSetting & (DeviceStates.InRange | DeviceStates.Tip)) != 0, NativeMethods.PenUsage);
            UpdateRegisterState(AppConfig.RegisterTouchPad, NativeMethods.TouchPadUsage);
        }

        private void UpdateRegisterState(bool register, ushort usage)
        {
            if (register)
            {
                RegisterDevice(usage);
            }
            else
            {
                UnregisterDevice(usage);
            }
        }

        private void RegisterDevice(ushort usage)
        {
            UnregisterDevice(usage);

            RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];

            rid[0].usUsagePage = NativeMethods.DigitizerUsagePage;
            rid[0].usUsage = usage;
            rid[0].dwFlags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY;
            rid[0].hwndTarget = Handle;

            if (!NativeMethods.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(rid[0])))
            {
                throw new ApplicationException("Failed to register raw input device(s).");
            }
            _registeredDeviceList.Add(usage);
        }

        private void UnregisterDevice(ushort usage)
        {
            if (_registeredDeviceList.Contains(usage))
            {
                RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];

                rid[0].usUsagePage = NativeMethods.DigitizerUsagePage;
                rid[0].usUsage = usage;
                rid[0].dwFlags = NativeMethods.RIDEV_REMOVE;
                rid[0].hwndTarget = IntPtr.Zero;

                if (!NativeMethods.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(rid[0])))
                {
                    throw new ApplicationException("Failed to unregister raw input device.");
                }
                _registeredDeviceList.Remove(usage);
            }
        }

        private bool ValidateDevice(IntPtr hDevice, out ushort usage)
        {
            usage = 0;
            uint pcbSize = 0;
            NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICEINFO, IntPtr.Zero, ref pcbSize);
            if (pcbSize <= 0)
                return false;

            IntPtr pInfo = Marshal.AllocHGlobal((int)pcbSize);
            using (new SafeUnmanagedMemoryHandle(pInfo))
            {
                NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICEINFO, pInfo, ref pcbSize);
                var info = (RID_DEVICE_INFO)Marshal.PtrToStructure(pInfo, typeof(RID_DEVICE_INFO));
                switch (info.hid.usUsage)
                {
                    case NativeMethods.TouchPadUsage:
                    case NativeMethods.TouchScreenUsage:
                    case NativeMethods.PenUsage:
                        break;
                    default:
                        return true;
                }

                NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref pcbSize);
                if (pcbSize <= 0)
                    return false;

                IntPtr pData = Marshal.AllocHGlobal((int)pcbSize);
                using (new SafeUnmanagedMemoryHandle(pData))
                {
                    NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, pData, ref pcbSize);
                    string deviceName = Marshal.PtrToStringAnsi(pData);

                    if (string.IsNullOrEmpty(deviceName) || deviceName.IndexOf("VIRTUAL_DIGITIZER", StringComparison.OrdinalIgnoreCase) >= 0 || deviceName.IndexOf("ROOT", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    usage = info.hid.usUsage;
                    return true;
                }
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                UpdateRegistration();
            }
        }

        protected override void WndProc(ref Message message)
        {
            switch (message.Msg)
            {
                case NativeMethods.WM_INPUT:
                    {
                        ProcessInputCommand(message.LParam);
                        break;
                    }
                case NativeMethods.WM_INPUT_DEVICE_CHANGE:
                    {
                        _validDevices.Clear();
                        break;
                    }
            }
            base.WndProc(ref message);
        }

        private void CheckLastError()
        {
            int errCode = Marshal.GetLastWin32Error();
            if (errCode != 0)
            {
                throw new Win32Exception(errCode);
            }
        }

        #region ProcessInput

        /// <summary>
        /// Processes WM_INPUT messages to retrieve information about any
        /// touch events that occur.
        /// </summary>
        /// <param name="LParam">The WM_INPUT message to process.</param>
        private void ProcessInputCommand(IntPtr LParam)
        {
            uint dwSize = 0;

            // First call to GetRawInputData sets the value of dwSize
            // dwSize can then be used to allocate the appropriate amount of memore,
            // storing the pointer in "buffer".
            NativeMethods.GetRawInputData(LParam, NativeMethods.RID_INPUT, IntPtr.Zero,
                             ref dwSize,
                             (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));

            IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
            try
            {
                // Check that buffer points to something, and if so,
                // call GetRawInputData again to fill the allocated memory
                // with information about the input
                if (buffer == IntPtr.Zero ||
                   NativeMethods.GetRawInputData(LParam, NativeMethods.RID_INPUT,
                                     buffer,
                                     ref dwSize,
                                     (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) != dwSize)
                {
                    throw new ApplicationException("GetRawInputData does not return correct size !\n.");
                }

                RAWINPUT raw = (RAWINPUT)Marshal.PtrToStructure(buffer, typeof(RAWINPUT));

                ushort usage;
                if (!_validDevices.TryGetValue(raw.header.hDevice, out usage))
                {
                    if (ValidateDevice(raw.header.hDevice, out usage))
                        _validDevices.Add(raw.header.hDevice, usage);
                }

                if (usage == 0)
                    return;
                if (usage == NativeMethods.PenUsage)
                {
                    if (_ignoreTouchInputWhenUsingPen)
                        _penLastActivity = Environment.TickCount;
                    else
                        _penLastActivity = null;

                    if (_penGestureButton == 0)
                        return;

                    switch (_sourceDevice)
                    {
                        case Devices.TouchScreen:
                        case Devices.None:
                        case Devices.Pen:
                            break;
                        default:
                            return;
                    }

                    uint pcbSize = 0;
                    NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref pcbSize);
                    IntPtr pPreparsedData = Marshal.AllocHGlobal((int)pcbSize);
                    using (new SafeUnmanagedMemoryHandle(pPreparsedData))
                    {
                        NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, pPreparsedData, ref pcbSize);
                        IntPtr pRawData = new IntPtr(buffer.ToInt64() + (raw.header.dwSize - raw.hid.dwSizHid * raw.hid.dwCount));

                        ushort[] usageList = GetButtonList(pPreparsedData, pRawData, 0, raw.hid.dwSizHid);
                        DeviceStates state = DeviceStates.None;
                        foreach (var u in usageList)
                        {
                            switch (u)
                            {
                                case NativeMethods.TipId:
                                    state |= DeviceStates.Tip;
                                    break;
                                case NativeMethods.InRangeId:
                                    state |= DeviceStates.InRange;
                                    break;
                                case NativeMethods.BarrelButtonId:
                                    state |= DeviceStates.RightClickButton;
                                    break;
                                case NativeMethods.InvertId:
                                    state |= DeviceStates.Invert;
                                    break;
                                case NativeMethods.EraserId:
                                    state |= DeviceStates.Eraser;
                                    break;
                                default:
                                    break;
                            }
                        }
                        if (_sourceDevice == Devices.None || _sourceDevice == Devices.TouchScreen)
                        {
                            if ((state & _penGestureButton) != 0)
                            {
                                _currentScr = Screen.FromPoint(Cursor.Position);
                                if (_currentScr == null)
                                    return;
                                _sourceDevice = Devices.Pen;
                                GetCurrentScreenOrientation();
                            }
                            else
                                return;
                        }
                        else if (_sourceDevice == Devices.Pen)
                        {
                            if ((state & _penGestureButton) == 0 || (state & DeviceStates.InRange) == 0)
                            {
                                state = DeviceStates.None;
                            }
                        }
                        if (_physicalMax.IsEmpty)
                            _physicalMax = GetPhysicalMax(1, pPreparsedData);

                        int physicalX = 0;
                        int physicalY = 0;

                        HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, 0, NativeMethods.XCoordinateId, ref physicalX, pPreparsedData, pRawData, raw.hid.dwSizHid);
                        HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, 0, NativeMethods.YCoordinateId, ref physicalY, pPreparsedData, pRawData, raw.hid.dwSizHid);

                        int x, y;
                        if (_isAxisCorresponds)
                        {
                            x = physicalX * _currentScr.Bounds.Width / _physicalMax.X;
                            y = physicalY * _currentScr.Bounds.Height / _physicalMax.Y;
                        }
                        else
                        {
                            x = physicalY * _currentScr.Bounds.Width / _physicalMax.Y;
                            y = physicalX * _currentScr.Bounds.Height / _physicalMax.X;
                        }
                        x = _xAxisDirection ? x : _currentScr.Bounds.Width - x;
                        y = _yAxisDirection ? y : _currentScr.Bounds.Height - y;
                        _outputTouchs = new List<RawData>(1);
                        _outputTouchs.Add(new RawData(state, 0, new Point(x + _currentScr.Bounds.X, y + _currentScr.Bounds.Y)));
                    }
                }
                else if (usage == NativeMethods.TouchScreenUsage)
                {
                    if (_penLastActivity != null && Environment.TickCount - _penLastActivity < 100)
                        return;
                    if (_sourceDevice == Devices.None)
                    {
                        _currentScr = Screen.FromPoint(Cursor.Position);
                        if (_currentScr == null)
                            return;
                        _sourceDevice = Devices.TouchScreen;
                        GetCurrentScreenOrientation();
                    }
                    else if (_sourceDevice != Devices.TouchScreen)
                        return;

                    uint pcbSize = 0;
                    NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref pcbSize);

                    IntPtr pPreparsedData = Marshal.AllocHGlobal((int)pcbSize);
                    using (new SafeUnmanagedMemoryHandle(pPreparsedData))
                    {
                        NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, pPreparsedData, ref pcbSize);

                        int contactCount = 0;
                        IntPtr pRawData = new IntPtr(buffer.ToInt64() + (raw.header.dwSize - raw.hid.dwSizHid * raw.hid.dwCount));
                        if (HidNativeApi.HIDP_STATUS_SUCCESS != HidNativeApi.HidP_GetUsageValue(HidReportType.Input, NativeMethods.DigitizerUsagePage, 0, NativeMethods.ContactCountId,
                            ref contactCount, pPreparsedData, pRawData, raw.hid.dwSizHid))
                        {
                            throw new ApplicationException(LocalizationProvider.Instance.GetTextValue("Messages.ContactCountError"));
                        }
                        int linkCount = 0;
                        HidNativeApi.HidP_GetLinkCollectionNodes(null, ref linkCount, pPreparsedData);
                        HidNativeApi.HIDP_LINK_COLLECTION_NODE[] lcn = new HidNativeApi.HIDP_LINK_COLLECTION_NODE[linkCount];
                        HidNativeApi.HidP_GetLinkCollectionNodes(lcn, ref linkCount, pPreparsedData);

                        if (_physicalMax.IsEmpty)
                            _physicalMax = GetPhysicalMax(linkCount, pPreparsedData);

                        if (contactCount != 0)
                        {
                            _requiringContactCount = contactCount;
                            _outputTouchs = new List<RawData>(contactCount);
                        }
                        if (_requiringContactCount == 0) return;
                        int contactIdentifier = 0;
                        int physicalX = 0;
                        int physicalY = 0;
                        for (int dwIndex = 0; dwIndex < raw.hid.dwCount; dwIndex++)
                        {
                            for (short nodeIndex = 1; nodeIndex <= lcn[0].NumberOfChildren; nodeIndex++)
                            {
                                IntPtr pRawDataPacket = new IntPtr(pRawData.ToInt64() + dwIndex * raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetUsageValue(HidReportType.Input, NativeMethods.DigitizerUsagePage, nodeIndex, NativeMethods.ContactIdentifierId, ref contactIdentifier, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, nodeIndex, NativeMethods.XCoordinateId, ref physicalX, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, nodeIndex, NativeMethods.YCoordinateId, ref physicalY, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);

                                ushort[] usageList = GetButtonList(pPreparsedData, pRawData, nodeIndex, raw.hid.dwSizHid);

                                int x, y;
                                if (_isAxisCorresponds)
                                {
                                    x = physicalX * _currentScr.Bounds.Width / _physicalMax.X;
                                    y = physicalY * _currentScr.Bounds.Height / _physicalMax.Y;
                                }
                                else
                                {
                                    x = physicalY * _currentScr.Bounds.Width / _physicalMax.Y;
                                    y = physicalX * _currentScr.Bounds.Height / _physicalMax.X;
                                }
                                x = _xAxisDirection ? x : _currentScr.Bounds.Width - x;
                                y = _yAxisDirection ? y : _currentScr.Bounds.Height - y;
                                bool tip = usageList.Length != 0 && usageList[0] == NativeMethods.TipId;
                                _outputTouchs.Add(new RawData(tip ? DeviceStates.Tip : DeviceStates.None, contactIdentifier, new Point(x + _currentScr.Bounds.X, y + _currentScr.Bounds.Y)));

                                if (--_requiringContactCount == 0) break;
                            }
                            if (_requiringContactCount == 0) break;
                        }
                    }
                }
                else if (usage == NativeMethods.TouchPadUsage)
                {
                    if (_sourceDevice == Devices.None)
                    {
                        _currentScr = Screen.FromPoint(Cursor.Position);
                        if (_currentScr == null)
                            return;
                        _sourceDevice = Devices.TouchPad;
                    }
                    else if (_sourceDevice != Devices.TouchPad)
                        return;

                    uint pcbSize = 0;
                    NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref pcbSize);
                    IntPtr pPreparsedData = Marshal.AllocHGlobal((int)pcbSize);
                    using (new SafeUnmanagedMemoryHandle(pPreparsedData))
                    {
                        NativeMethods.GetRawInputDeviceInfo(raw.header.hDevice, NativeMethods.RIDI_PREPARSEDDATA, pPreparsedData, ref pcbSize);

                        int contactCount = 0;
                        IntPtr pRawData = new IntPtr(buffer.ToInt64() + (raw.header.dwSize - raw.hid.dwSizHid * raw.hid.dwCount));
                        if (HidNativeApi.HIDP_STATUS_SUCCESS != HidNativeApi.HidP_GetUsageValue(HidReportType.Input, NativeMethods.DigitizerUsagePage, 0, NativeMethods.ContactCountId,
                            ref contactCount, pPreparsedData, pRawData, raw.hid.dwSizHid))
                        {
                            throw new ApplicationException(LocalizationProvider.Instance.GetTextValue("Messages.ContactCountError"));
                        }

                        int linkCount = 0;
                        HidNativeApi.HidP_GetLinkCollectionNodes(null, ref linkCount, pPreparsedData);
                        HidNativeApi.HIDP_LINK_COLLECTION_NODE[] lcn = new HidNativeApi.HIDP_LINK_COLLECTION_NODE[linkCount];
                        HidNativeApi.HidP_GetLinkCollectionNodes(lcn, ref linkCount, pPreparsedData);

                        if (_physicalMax.IsEmpty)
                            _physicalMax = GetPhysicalMax(linkCount, pPreparsedData);

                        if (contactCount != 0)
                        {
                            _requiringContactCount = contactCount;
                            _outputTouchs = new List<RawData>(contactCount);
                        }
                        if (_requiringContactCount == 0) return;

                        int contactIdentifier = 0;
                        int physicalX = 0;
                        int physicalY = 0;

                        for (int dwIndex = 0; dwIndex < raw.hid.dwCount; dwIndex++)
                        {
                            for (short nodeIndex = 1; nodeIndex <= lcn[0].NumberOfChildren; nodeIndex++)
                            {
                                IntPtr pRawDataPacket = new IntPtr(pRawData.ToInt64() + dwIndex * raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetUsageValue(HidReportType.Input, NativeMethods.DigitizerUsagePage, nodeIndex, NativeMethods.ContactIdentifierId, ref contactIdentifier, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, nodeIndex, NativeMethods.XCoordinateId, ref physicalX, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);
                                HidNativeApi.HidP_GetScaledUsageValue(HidReportType.Input, NativeMethods.GenericDesktopPage, nodeIndex, NativeMethods.YCoordinateId, ref physicalY, pPreparsedData, pRawDataPacket, raw.hid.dwSizHid);

                                ushort[] usageList = GetButtonList(pPreparsedData, pRawData, nodeIndex, raw.hid.dwSizHid);

                                int x, y;
                                x = physicalX * _currentScr.Bounds.Width / _physicalMax.X;
                                y = physicalY * _currentScr.Bounds.Height / _physicalMax.Y;

                                bool tip = usageList.Length != 0 && usageList[0] == NativeMethods.TipId;
                                _outputTouchs.Add(new RawData(tip ? DeviceStates.Tip : DeviceStates.None, contactIdentifier, new Point(x + _currentScr.Bounds.X, y + _currentScr.Bounds.Y)));

                                if (--_requiringContactCount == 0) break;
                            }
                            if (_requiringContactCount == 0) break;
                        }
                    }
                }

                if (_requiringContactCount == 0 && PointsIntercepted != null)
                {
                    PointsIntercepted(this, new RawPointsDataMessageEventArgs(_outputTouchs, _sourceDevice));
                    if (_outputTouchs.TrueForAll(rd => rd.State == DeviceStates.None))
                    {
                        _sourceDevice = Devices.None;
                        _physicalMax = Point.Empty;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ushort[] GetButtonList(IntPtr pPreparsedData, IntPtr pRawData, short nodeIndex, int rawDateSize)
        {
            int usageLength = 0;
            HidNativeApi.HidP_GetUsages(HidReportType.Input, NativeMethods.DigitizerUsagePage, nodeIndex, null, ref usageLength, pPreparsedData, pRawData, rawDateSize);
            var usageList = new ushort[usageLength];
            HidNativeApi.HidP_GetUsages(HidReportType.Input, NativeMethods.DigitizerUsagePage, nodeIndex, usageList, ref usageLength, pPreparsedData, pRawData, rawDateSize);
            return usageList;
        }

        private void GetCurrentScreenOrientation()
        {
            switch (SystemInformation.ScreenOrientation)
            {
                case ScreenOrientation.Angle0:
                    _xAxisDirection = _yAxisDirection = true;
                    _isAxisCorresponds = true;
                    break;
                case ScreenOrientation.Angle90:
                    _isAxisCorresponds = false;
                    _xAxisDirection = false;
                    _yAxisDirection = true;
                    break;
                case ScreenOrientation.Angle180:
                    _xAxisDirection = _yAxisDirection = false;
                    _isAxisCorresponds = true;
                    break;
                case ScreenOrientation.Angle270:
                    _isAxisCorresponds = false;
                    _xAxisDirection = true;
                    _yAxisDirection = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private Point GetPhysicalMax(int collectionCount, IntPtr pPreparsedData)
        {
            short valueCapsLength = (short)(collectionCount > 0 ? collectionCount : 1);
            Point p = new Point();
            HidNativeApi.HidP_Value_Caps[] hvc = new HidNativeApi.HidP_Value_Caps[valueCapsLength];

            HidNativeApi.HidP_GetSpecificValueCaps(HidReportType.Input, NativeMethods.GenericDesktopPage, 0, NativeMethods.XCoordinateId, hvc, ref valueCapsLength, pPreparsedData);
            p.X = hvc[0].PhysicalMax != 0 ? hvc[0].PhysicalMax : hvc[0].LogicalMax;

            HidNativeApi.HidP_GetSpecificValueCaps(HidReportType.Input, NativeMethods.GenericDesktopPage, 0, NativeMethods.YCoordinateId, hvc, ref valueCapsLength, pPreparsedData);
            p.Y = hvc[0].PhysicalMax != 0 ? hvc[0].PhysicalMax : hvc[0].LogicalMax;
            return p;
        }

        #endregion ProcessInput
    }
}

