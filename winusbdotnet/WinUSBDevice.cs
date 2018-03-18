﻿/*
Copyright (c) 2014 Stephen Stair (sgstair@akkit.org)

Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;

namespace winusbdotnet
{

    public class WinUSBEnumeratedDevice
    {
        internal string DevicePath;
        internal EnumeratedDevice EnumeratedData;
        internal WinUSBEnumeratedDevice(EnumeratedDevice enumDev)
        {
            DevicePath = enumDev.DevicePath;
            EnumeratedData = enumDev;
            Match m = Regex.Match(DevicePath, @"vid_([\da-f]{4})");
            if (m.Success) { VendorID = Convert.ToUInt16(m.Groups[1].Value, 16); }
            m = Regex.Match(DevicePath, @"pid_([\da-f]{4})");
            if (m.Success) { ProductID = Convert.ToUInt16(m.Groups[1].Value, 16); }
            m = Regex.Match(DevicePath, @"mi_([\da-f]{2})");
            if (m.Success) { UsbInterface = Convert.ToByte(m.Groups[1].Value, 16); }
        }

        public string Path { get { return DevicePath; } }
        public UInt16 VendorID { get; private set; }
        public UInt16 ProductID { get; private set; }
        public Byte UsbInterface { get; private set; }
        public Guid InterfaceGuid { get { return EnumeratedData.InterfaceGuid; } }


        public override string ToString()
        {
            return string.Format("WinUSBEnumeratedDevice({0},{1})", DevicePath, InterfaceGuid);
        }
    }

    public enum UsbStandardRequestCode
    {
        GetStatus = 0,
        ClearFeature = 1,
        SetFeature = 3,
        SetAddress = 5,
        GetDescriptor = 6,
        SetDescriptor = 7,
        GetConfiguration = 8,
        SetConfiguration = 9,
        GetInterface = 10,
        SetInterface = 11
    }

    public class WinUSBDevice : IDisposable
    {
        public static IEnumerable<WinUSBEnumeratedDevice> EnumerateDevices(Guid deviceInterfaceGuid)
        {
            foreach (EnumeratedDevice devicePath in NativeMethods.EnumerateDevicesByInterface(deviceInterfaceGuid))
            {
                yield return new WinUSBEnumeratedDevice(devicePath);
            }
        }

        public static IEnumerable<WinUSBEnumeratedDevice> EnumerateAllDevices()
        {
            foreach (EnumeratedDevice devicePath in NativeMethods.EnumerateAllWinUsbDevices())
            {
                yield return new WinUSBEnumeratedDevice(devicePath);
            }
        }
        public delegate void NewDataCallback();

        string myDevicePath;
        SafeFileHandle deviceHandle;
        IntPtr WinusbHandle;

        internal bool Stopping = false;

        public WinUSBDevice(WinUSBEnumeratedDevice deviceInfo)
        {
            myDevicePath = deviceInfo.DevicePath;

            deviceHandle = NativeMethods.CreateFile(myDevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (deviceHandle.IsInvalid)
            {
                throw new Exception("Could not create file. " + (new Win32Exception()).ToString());
            }

            if (!NativeMethods.WinUsb_Initialize(deviceHandle, out WinusbHandle))
            {
                WinusbHandle = IntPtr.Zero;
                throw new Exception("Could not Initialize WinUSB. " + (new Win32Exception()).ToString());
            }


        }


        public byte AlternateSetting
        {
            get
            {
                byte alt;
                if (!NativeMethods.WinUsb_GetCurrentAlternateSetting(WinusbHandle, out alt))
                {
                    throw new Exception("GetCurrentAlternateSetting failed. " + (new Win32Exception()).ToString());
                }
                return alt;
            }
            set
            {
                if (!NativeMethods.WinUsb_SetCurrentAlternateSetting(WinusbHandle, value))
                {
                    throw new Exception("SetCurrentAlternateSetting failed. " + (new Win32Exception()).ToString());
                }
            }
        }

        public DeviceDescriptor GetDeviceDescriptor()
        {
            return DeviceDescriptor.Parse(GetDescriptor(DescriptorType.Device, 0, 32));
        }

        public ConfigurationDescriptor GetConfigurationDescriptor()
        {
            // Todo: more than just the first configuration.
            return ConfigurationDescriptor.Parse(GetDescriptor(DescriptorType.Configuration, 0));
        }


        public byte[] GetDescriptor(DescriptorType descriptorType, byte descriptorIndex, ushort length = 1024)
        {
            UInt16 value = (UInt16)(descriptorIndex | ((byte)descriptorType) << 8);
            return ControlTransferIn(ControlTypeStandard | ControlRecipientDevice, (byte)UsbStandardRequestCode.GetDescriptor, value, 0, length);
        }

        public void Dispose()
        {
            Stopping = true;

            // Abort pipe IO for any background threads
            foreach (BufferedPipeThread th in bufferedPipes.Values)
            {
                NativeMethods.WinUsb_AbortPipe(WinusbHandle, th.PipeID);
            }

            // Wait for pipe threads to quit
            foreach (BufferedPipeThread th in bufferedPipes.Values)
            {
                th.Join(TimeSpan.FromMilliseconds(500)); // Give threads a little time to wind down.
            }

            // Close handles
            if (WinusbHandle != IntPtr.Zero)
            {
                NativeMethods.WinUsb_Free(WinusbHandle);
                WinusbHandle = IntPtr.Zero;
            }
            deviceHandle.Close();

            GC.SuppressFinalize(this);
        }

        public void Close()
        {
            Dispose();
        }

        public void FlushPipe(byte pipeId)
        {
            if (!NativeMethods.WinUsb_FlushPipe(WinusbHandle, pipeId))
            {
                throw new Exception("FlushPipe failed. " + (new Win32Exception()).ToString());
            }
        }

        public UInt32 GetPipePolicy(byte pipeId, WinUsbPipePolicy policyType)
        {
            UInt32[] data = new UInt32[1];
            UInt32 length = 4;

            if (!NativeMethods.WinUsb_GetPipePolicy(WinusbHandle, pipeId, (uint)policyType, ref length, data))
            {
                throw new Exception("GetPipePolicy failed. " + (new Win32Exception()).ToString());
            }

            return data[0];
        }

        public void SetPipePolicy(byte pipeId, WinUsbPipePolicy policyType, UInt32 newValue)
        {
            UInt32[] data = new UInt32[1];
            UInt32 length = 4;
            data[0] = newValue;

            if (!NativeMethods.WinUsb_SetPipePolicy(WinusbHandle, pipeId, (uint)policyType, length, data))
            {
                throw new Exception("SetPipePolicy failed. " + (new Win32Exception()).ToString());
            }
        }

        Dictionary<byte, BufferedPipeThread> bufferedPipes = new Dictionary<byte, BufferedPipeThread>();
        // Todo: Compute better value for bufferLength. Based on pipe transfer size.
        public void EnableBufferedRead(byte pipeId, int bufferCount = 16, int bufferLength = 32)
        {
            if (!bufferedPipes.ContainsKey(pipeId))
            {
                bufferedPipes.Add(pipeId, new BufferedPipeThread(this, pipeId, bufferCount, bufferLength));
            }
        }

        public void StopBufferedRead(byte pipeId)
        {
            throw new NotImplementedException();
        }

        public void BufferedReadNotifyPipe(byte pipeId, NewDataCallback callback)
        {
            if (!bufferedPipes.ContainsKey(pipeId))
            {
                throw new Exception("Pipe not enabled for buffered reads!");
            }
            bufferedPipes[pipeId].NewDataEvent += callback;
        }

        BufferedPipeThread GetInterface(byte pipeId, bool packetInterface)
        {
            if (!bufferedPipes.ContainsKey(pipeId))
            {
                throw new Exception("Pipe not enabled for buffered reads!");
            }
            BufferedPipeThread th = bufferedPipes[pipeId];
            if (!th.InterfaceBound)
            {
                th.InterfaceBound = true;
                th.PacketInterface = packetInterface;
            }
            else
            {
                if (th.PacketInterface != packetInterface)
                {
                    string message = string.Format("Pipe is already bound as a {0} interface - cannot bind to both Packet and Byte interfaces",
                                                   packetInterface ? "Byte" : "Packet");
                    throw new Exception(message);
                }
            }
            return th;
        }
        public IPipeByteReader BufferedGetByteInterface(byte pipeId)
        {
            return GetInterface(pipeId, false);
        }

        public IPipePacketReader BufferedGetPacketInterface(byte pipeId)
        {
            return GetInterface(pipeId, true);
        }



        public byte[] BufferedReadPipe(byte pipeId, int byteCount)
        {
            return BufferedGetByteInterface(pipeId).ReceiveBytes(byteCount);
        }

        public byte[] BufferedPeekPipe(byte pipeId, int byteCount)
        {
            return BufferedGetByteInterface(pipeId).PeekBytes(byteCount);
        }

        public void BufferedSkipBytesPipe(byte pipeId, int byteCount)
        {
            BufferedGetByteInterface(pipeId).SkipBytes(byteCount);
        }

        public byte[] BufferedReadExactPipe(byte pipeId, int byteCount)
        {
            return BufferedGetByteInterface(pipeId).ReceiveExactBytes(byteCount);
        }

        public int BufferedByteCountPipe(byte pipeId)
        {
            return BufferedGetByteInterface(pipeId).QueuedDataLength;
        }


        public byte[] ReadExactPipe(byte pipeId, int byteCount)
        {
            int read = 0;
            byte[] accumulate = null;
            while (read < byteCount)
            {
                byte[] data = ReadPipe(pipeId, byteCount - read);
                if (data.Length == 0)
                {
                    // Timeout happened in ReadPipe.
                    throw new Exception("Timed out while trying to read data.");
                }
                if (data.Length == byteCount) return data;
                if (accumulate == null)
                {
                    accumulate = new byte[byteCount];
                }
                Array.Copy(data, 0, accumulate, read, data.Length);
                read += data.Length;
            }
            return accumulate;
        }

        // basic synchronous read
        public byte[] ReadPipe(byte pipeId, int byteCount)
        {

            byte[] data = new byte[byteCount];

            UInt32 transferSize = 0;
            if (!NativeMethods.WinUsb_ReadPipe(WinusbHandle, pipeId, data, (uint)byteCount, ref transferSize, IntPtr.Zero))
            {
                if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_SEM_TIMEOUT)
                {
                    // This was a pipe timeout. Return an empty byte array to indicate this case.
                    return new byte[0];
                }
                throw new Exception("ReadPipe failed. " + (new Win32Exception()).ToString());
            }

            byte[] newdata = new byte[transferSize];
            Array.Copy(data, newdata, transferSize);
            return newdata;

        }

        // Asynchronous read bits, only for use with buffered reader for now.
        internal void BeginReadPipe(byte pipeId, QueuedBuffer buffer)
        {
            buffer.Overlapped.WaitEvent.Reset();

            if (!NativeMethods.WinUsb_ReadPipe(WinusbHandle, pipeId, buffer.PinnedBuffer, (uint)buffer.BufferSize, IntPtr.Zero, buffer.Overlapped.OverlappedStruct))
            {
                if (Marshal.GetLastWin32Error() != NativeMethods.ERROR_IO_PENDING)
                {
                    throw new Exception("ReadPipe failed. " + (new Win32Exception()).ToString());
                }
            }
        }

        internal bool EndReadPipe(QueuedBuffer buf)
        {
            UInt32 transferSize;

            if (!NativeMethods.WinUsb_GetOverlappedResult(WinusbHandle, buf.Overlapped.OverlappedStruct, out transferSize, true))
            {
                if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_SEM_TIMEOUT)
                {
                    // This was a pipe timeout. Return an empty byte array to indicate this case.
                    //System.Diagnostics.Debug.WriteLine("Timed out");
                    return false;
                }
                throw new Exception("ReadPipe's overlapped result failed. " + (new Win32Exception()).ToString());
            }
            buf.CompletedSize = (int)transferSize;
            return true;
        }


        // basic synchronous send.
        public void WritePipe(byte pipeId, byte[] pipeData)
        {

            int remainingbytes = pipeData.Length;
            while (remainingbytes > 0)
            {

                UInt32 transferSize = 0;
                if (!NativeMethods.WinUsb_WritePipe(WinusbHandle, pipeId, pipeData, (uint)pipeData.Length, ref transferSize, IntPtr.Zero))
                {
                    throw new Exception("WritePipe failed. " + (new Win32Exception()).ToString());
                }
                if (transferSize == pipeData.Length) return;

                remainingbytes -= (int)transferSize;

                // Need to retry. Copy the remaining data to a new buffer.
                byte[] data = new byte[remainingbytes];
                Array.Copy(pipeData, transferSize, data, 0, remainingbytes);

                pipeData = data;
            }
        }



        public void ControlTransferOut(byte requestType, byte request, UInt16 value, UInt16 index, byte[] data)
        {
            NativeMethods.WINUSB_SETUP_PACKET setupPacket = new NativeMethods.WINUSB_SETUP_PACKET();
            setupPacket.RequestType = (byte)(requestType | ControlDirectionOut);
            setupPacket.Request = request;
            setupPacket.Value = value;
            setupPacket.Index = index;
            if (data != null)
            {
                setupPacket.Length = (ushort)data.Length;
            }

            UInt32 actualLength = 0;

            if (!NativeMethods.WinUsb_ControlTransfer(WinusbHandle, setupPacket, data, setupPacket.Length, out actualLength, IntPtr.Zero))
            {
                throw new Exception("ControlTransfer failed. " + (new Win32Exception()).ToString());
            }
            
            if (data != null && actualLength != data.Length)
            {
                throw new Exception("Not all data transferred");
            }
        }

        public byte[] ControlTransferIn(byte requestType, byte request, UInt16 value, UInt16 index, UInt16 length)
        {
            NativeMethods.WINUSB_SETUP_PACKET setupPacket = new NativeMethods.WINUSB_SETUP_PACKET();
            setupPacket.RequestType = (byte)(requestType | ControlDirectionIn);
            setupPacket.Request = request;
            setupPacket.Value = value;
            setupPacket.Index = index;
            setupPacket.Length = length;

            byte[] output = new byte[length];
            UInt32 actualLength = 0;

            if(!NativeMethods.WinUsb_ControlTransfer(WinusbHandle, setupPacket, output, (uint)output.Length, out actualLength, IntPtr.Zero))
            {
                throw new Exception("ControlTransfer failed. " + (new Win32Exception()).ToString());
            }

            if(actualLength != output.Length)
            {
                byte[] copyTo = new byte[actualLength];
                Array.Copy(output, copyTo, actualLength);
                output = copyTo;
            }
            return output;
        }

        const byte ControlDirectionOut = 0x00;
        const byte ControlDirectionIn = 0x80;

        public const byte ControlTypeStandard = 0x00;
        public const byte ControlTypeClass = 0x20;
        public const byte ControlTypeVendor = 0x40;

        public const byte ControlRecipientDevice = 0;
        public const byte ControlRecipientInterface = 1;
        public const byte ControlRecipientEndpoint = 2;
        public const byte ControlRecipientOther = 3;


    }


    internal class QueuedBuffer : IDisposable
    {
        public readonly int BufferSize;
        public int CompletedSize;
        public Overlapped Overlapped;
        public IntPtr PinnedBuffer;
        public QueuedBuffer(int bufferSizeBytes)
        {
            BufferSize = bufferSizeBytes;
            Overlapped = new Overlapped();
            PinnedBuffer = Marshal.AllocHGlobal(BufferSize);
        }

        public void Dispose()
        {
            Overlapped.Dispose();
            Marshal.FreeHGlobal(PinnedBuffer);
            GC.SuppressFinalize(this);
        }

        public void Wait()
        {
            Overlapped.WaitEvent.WaitOne();
        }

        public bool Ready
        {
            get
            {
                return Overlapped.WaitEvent.WaitOne(0);
            }
        }
        
    }



    public interface IPipeByteReader
    {
        /// <summary>
        /// Receive a number of bytes from the incoming data stream.
        /// If there are not enough bytes available, only the available bytes will be returned.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] ReceiveBytes(int count);

        /// <summary>
        /// Receive a number of bytes from the incoming data stream, but don't remove them from the queue.
        /// If there are not enough bytes available, only the available bytes will be returned.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] PeekBytes(int count);

        /// <summary>
        /// Receive a specific number of bytes from the incoming data stream.
        /// This call will block until the requested bytes are available, or will eventually throw on timeout.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] ReceiveExactBytes(int count);

        /// <summary>
        /// Drop bytes from the incoming data stream without reading them.
        /// If you try to drop more bytes than are available, the buffer will be cleared.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to drop.</param>
        void SkipBytes(int count);

        /// <summary>
        /// Current number of bytes that are queued and available to read.
        /// </summary>
        int QueuedDataLength { get; }
    }

    public interface IPipePacketReader
    {
        /// <summary>
        /// Number of received packets that can be read.
        /// </summary>
        int QueuedPackets { get; }

        /// <summary>
        /// Length of the next packet waiting to be dequeued.
        /// </summary>
        int NextPacketLength { get; }

        /// <summary>
        /// Retrieve the next packet from the receive queue into a buffer.
        /// </summary>
        /// <returns>The length that was copied</returns>
        int ReadPacket(byte[] target, int offset);
    }


    // Background thread to receive data from pipes.
    // Provides two data access mechanisms which are mutually exclusive: Packet level and byte level.
    internal class BufferedPipeThread : IPipeByteReader, IPipePacketReader
    {
        // Logic to enforce interface exclucivity is in WinUSBDevice
        public bool InterfaceBound; // Has the interface been bound?
        public bool PacketInterface; // Are we using the packet reader interface?

        public byte PipeID {  get { return DevicePipeId; } }

        Thread PipeThread;
        Thread WorkerThread;
        AutoResetEvent ThreadNewData;
        WinUSBDevice Device;
        byte DevicePipeId;

        private long TotalReceived;

        private int QueuedLength;
        private Queue<byte[]> ReceivedData;
        private int SkipFirstBytes;
        public bool Stopped = false;


        ManualResetEvent ReceiveTick;

        QueuedBuffer[] BufferList;
        Queue<QueuedBuffer> PendingBuffers;
        Queue<QueuedBuffer> ReceivedBuffers;
        Queue<QueuedBuffer> RequeuePending;
        object BufferLock;


        public BufferedPipeThread(WinUSBDevice dev, byte pipeId, int bufferCount, int bufferSize)
        {


            int maxTransferSize = (int)dev.GetPipePolicy(pipeId, WinUsbPipePolicy.MAXIMUM_TRANSFER_SIZE);

            if (bufferSize > maxTransferSize) { bufferSize = maxTransferSize; }

            BufferLock = new object();
            PendingBuffers = new Queue<QueuedBuffer>(bufferCount);
            ReceivedBuffers = new Queue<QueuedBuffer>(bufferCount);
            RequeuePending = new Queue<QueuedBuffer>(bufferCount);
            BufferList = new QueuedBuffer[bufferCount];
            for (int i = 0; i < bufferCount;i++)
            {
                BufferList[i] = new QueuedBuffer(bufferSize);
            }

            Device = dev;
            DevicePipeId = pipeId;
            QueuedLength = 0;
            ReceivedData = new Queue<byte[]>();
            ReceiveTick = new ManualResetEvent(false);
            PipeThread = new Thread(ThreadFunc);
            PipeThread.IsBackground = true;
            WorkerThread = new Thread(WorkerThreadFunc);
            WorkerThread.IsBackground = true;
            ThreadNewData = new AutoResetEvent(false);


            //dev.SetPipePolicy(pipeId, WinUsbPipePolicy.PIPE_TRANSFER_TIMEOUT, 1000);

            // Start reading on all the buffers.
            foreach(QueuedBuffer qb in BufferList)
            {
                dev.BeginReadPipe(pipeId, qb);
                PendingBuffers.Enqueue(qb);
            }

            //dev.SetPipePolicy(pipeId, WinUsbPipePolicy.RAW_IO, 1);

            PipeThread.Start();
            WorkerThread.Start();
        }

        // Wait for the threads associated with this class to terminate. Call this after setting the device Stopping flag.
        public void Join(TimeSpan timeout)
        {
            ThreadNewData.Set(); // Ensure worker thread exits.
            PipeThread.Join(timeout);
            WorkerThread.Join(timeout);
        }

        public long TotalReceivedBytes { get { return TotalReceived; } }

        //
        // Packet Reader members
        //

        public int QueuedPackets { get { lock (this) { return ReceivedBuffers.Count; } } }

        public int NextPacketLength
        {
            get
            {
                lock (BufferLock)
                {
                    if (ReceivedBuffers.Count > 0) return ReceivedBuffers.Peek().CompletedSize;
                }
                return 0;
            }
        }


        public int ReadPacket(byte[] target, int offset)
        {
            QueuedBuffer buf = null;
            lock(BufferLock)
            {
                if(ReceivedBuffers.Count > 0)
                {
                    buf = ReceivedBuffers.Dequeue();
                }
                else
                {
                    return 0;
                }
            }
            int length = buf.CompletedSize;
            Marshal.Copy(buf.PinnedBuffer, target, offset, buf.CompletedSize);
            lock (RequeuePending)
            {
                RequeuePending.Enqueue(buf);
            }
            return length;
        }

        void UpdateReceivedData()
        {
            lock (this)
            {
                while (NextPacketLength > 0)
                {
                    byte[] buffer = new byte[NextPacketLength];
                    ReadPacket(buffer, 0);
                    ReceivedData.Enqueue(buffer);
                }
            }
        }


        //
        // Byte Reader members
        //

        public int QueuedDataLength { get {  return QueuedLength;  } }

        // Only returns as many as it can.
        public byte[] ReceiveBytes(int count)
        {
            int queue = QueuedDataLength;
            if (queue < count) 
                count = queue;

            byte[] output = new byte[count];
            lock (this)
            {
                UpdateReceivedData();
                CopyReceiveBytes(output, 0, count);
            }
            return output;
        }

        // Only returns as many as it can.
        public byte[] PeekBytes(int count)
        {
            int queue = QueuedDataLength;
            if (queue < count)
                count = queue;

            byte[] output = new byte[count];
            lock (this)
            {
                UpdateReceivedData();
                CopyPeekBytes(output, 0, count);
            }
            return output;
        }

        public byte[] ReceiveExactBytes(int count)
        {
            byte[] output = new byte[count];
            if (QueuedDataLength >= count)
            {
                lock (this)
                {
                    UpdateReceivedData();
                    CopyReceiveBytes(output, 0, count);
                }
                return output;
            }
            int failedcount = 0;
            int haveBytes = 0;
            while (haveBytes < count)
            {
                ReceiveTick.Reset();
                lock (this)
                {
                    UpdateReceivedData();
                    int thisBytes = QueuedLength;

                    if(thisBytes == 0)
                    {
                        failedcount++;
                        if(failedcount > 3)
                        {
                            throw new Exception("Timed out waiting to receive bytes");
                        }
                    }
                    else
                    {
                        failedcount = 0;
                        if (thisBytes + haveBytes > count) thisBytes = count - haveBytes;
                        CopyReceiveBytes(output, haveBytes, thisBytes);
                    }
                    haveBytes += (int)thisBytes;
                }
                if(haveBytes < count)
                {
                    if (Stopped) throw new Exception("Not going to have enough bytes to complete request.");
                    ReceiveTick.WaitOne();
                }
            }
            return output;
        }

        public void SkipBytes(int count)
        {
            lock (this)
            {
                UpdateReceivedData();
                int queue = QueuedLength;
                if (queue < count)
                    throw new ArgumentException("count must be less than the data length");

                int copied = 0;
                while (copied < count)
                {
                    byte[] firstData = ReceivedData.Peek();
                    int available = firstData.Length - SkipFirstBytes;
                    int toCopy = count - copied;
                    if (toCopy > available) toCopy = available;

                    if (toCopy == available)
                    {
                        ReceivedData.Dequeue();
                        SkipFirstBytes = 0;
                    }
                    else
                    {
                        SkipFirstBytes += toCopy;
                    }

                    copied += toCopy;
                    QueuedLength -= toCopy;
                }
            }
        }

        //
        // Internal functionality
        //

        // Must be called under lock with enough bytes in the buffer.
        void CopyReceiveBytes(byte[] target, int start, int count)
        {
            int copied = 0;
            while(copied < count)
            {
                byte[] firstData = ReceivedData.Peek();
                int available = firstData.Length - SkipFirstBytes;
                int toCopy = count - copied;
                if (toCopy > available) toCopy = available;

                Array.Copy(firstData, SkipFirstBytes, target, start, toCopy); 

                if(toCopy == available)
                {
                    ReceivedData.Dequeue();
                    SkipFirstBytes = 0;
                }
                else
                {
                    SkipFirstBytes += toCopy;
                }

                copied += toCopy;
                start += toCopy;
                QueuedLength -= toCopy;
            }
        }

        // Must be called under lock with enough bytes in the buffer.
        void CopyPeekBytes(byte[] target, int start, int count)
        {
            int copied = 0;
            int skipBytes = SkipFirstBytes;

            foreach(byte[] firstData in ReceivedData)
            {
                int available = firstData.Length - skipBytes;
                int toCopy = count - copied;
                if (toCopy > available) toCopy = available;

                Array.Copy(firstData, skipBytes, target, start, toCopy);

                skipBytes = 0;

                copied += toCopy;
                start += toCopy;

                if (copied >= count)
                {
                    break;
                }
            }
        }




        void ThreadFunc(object context)
        {
            int recvBytes;
            while(true)
            {
                if (Device.Stopping)
                    break;

                try
                {
                    recvBytes = 0;
                    if (PendingBuffers.Count > 0)
                    {
                        PendingBuffers.Peek().Wait();
                    }
                    // Process a large group of received buffers in a batch, if available.
                    int n = 0;
                    bool shortcut = PendingBuffers.Count > 0;
                    try
                    {
                        lock(RequeuePending)
                        {
                            // Requeue buffers that were drained.
                            while(RequeuePending.Count > 0)
                            {
                                QueuedBuffer buf = RequeuePending.Dequeue();
                                Device.BeginReadPipe(DevicePipeId, buf);
                                // Todo: If this operation fails during normal operation, the buffer is lost from rotation.
                                // Should never happen during normal operation, but should confirm and mitigate if it's possible.
                                PendingBuffers.Enqueue(buf);
                            }
                        }
                        if (PendingBuffers.Count == 0)
                        {
                            Thread.Sleep(0);
                        }
                        else
                        {
                            lock (BufferLock)
                            {
                                while (n < BufferList.Length && PendingBuffers.Count > 0)
                                {
                                    QueuedBuffer buf = PendingBuffers.Peek();
                                    if (shortcut || buf.Ready)
                                    {
                                        shortcut = false;
                                        PendingBuffers.Dequeue();
                                        if (Device.EndReadPipe(buf))
                                        {
                                            ReceivedBuffers.Enqueue(buf);
                                            recvBytes += buf.CompletedSize;
                                        }
                                        else
                                        {
                                            // Timeout condition. Requeue.
                                            Device.BeginReadPipe(DevicePipeId, buf);
                                            // Todo: If this operation fails during normal operation, the buffer is lost from rotation.
                                            // Should never happen during normal operation, but should confirm and mitigate if it's possible.
                                            PendingBuffers.Enqueue(buf);
                                        }
                                    }
                                    n++;
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Unless we're exiting, ensure we always indicate the data, even if some operation failed.
                        if(!Device.Stopping && recvBytes > 0)
                        {
                            lock(this)
                            {
                                QueuedLength += recvBytes;
                                TotalReceived += recvBytes;
                            }
                            ThreadNewData.Set();
                            //ThreadPool.QueueUserWorkItem(RaiseNewData);
                            
                        }
                    }
                }
                catch(Exception ex)
                {
                    System.Diagnostics.Debug.Print("Should not happen: Exception in background thread. {0}", ex.ToString());
                    Thread.Sleep(15);
                }

                ReceiveTick.Set();

            }
            Stopped = true;
        }

        public event WinUSBDevice.NewDataCallback NewDataEvent;



        void WorkerThreadFunc()
        {
            // Attempt to set processor affinity to everything but the first two. (todo: come up with something smarter.)

            Thread.BeginThreadAffinity();
            if (Environment.ProcessorCount > 2)
            {
#pragma warning disable 618
                // warning CS0618: 'System.AppDomain.GetCurrentThreadId()' is obsolete: 'AppDomain.GetCurrentThreadId has been deprecated because it does not provide a stable Id when managed threads are running on fibers (aka lightweight threads). To get a stable identifier for a managed thread, use the ManagedThreadId property on Thread.  http://go.microsoft.com/fwlink/?linkid=14202'
                int threadId = AppDomain.GetCurrentThreadId();
#pragma warning restore 618
                int cpuCount = Environment.ProcessorCount;
                long cpuMask = -4;
                if(cpuCount == 63)
                {
                    cpuMask = 0x7FFFFFFFFFFFFFFCL;
                }
                if(cpuCount < 63)
                {
                    cpuMask = (2 << cpuCount) - 1;
                    cpuMask -= 3;
                }
                ProcessThread thread = Process.GetCurrentProcess().Threads.Cast<ProcessThread>().Where(t => t.Id == threadId).Single();
                thread.ProcessorAffinity = new IntPtr(cpuMask);
            }

            while (true)
            {
                if (Device.Stopping)
                    break;


                if (ThreadNewData.WaitOne(1000))
                {
                    RaiseNewData(null);
                }
            }
            Thread.EndThreadAffinity();
        }

        void RaiseNewData(object context)
        {
            WinUSBDevice.NewDataCallback cb = NewDataEvent;
            if (cb != null)
            {
                long dataMarker = -1;
                while (dataMarker != TotalReceivedBytes)
                {
                    dataMarker = TotalReceivedBytes;
                    cb();
                }
            }
        }

    }

}
