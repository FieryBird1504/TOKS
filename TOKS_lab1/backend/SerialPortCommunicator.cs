using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using TOKS_lab1.backend.exceptions;
using TOKS_lab1.Enums;

namespace TOKS_lab1.backend
{
    public class SerialPortCommunicator
    {
        private SerialPort _serialPort;
        private const int BitsInByte = 8;
        private const byte StartStopByte = 0x55;

        private string BitStaffingSeqBeforeStaffing = "101010";
        private string BitStaffingSeqAfterStaffing = "1010100";

        private const int DataInPacketSizeInBytes = 16;
        private const int PacketSizeInBytesWithoutStartByte = DataInPacketSizeInBytes + 3;
        private const byte EmptyByte = 0x00;
        private string _receivedBuffer = string.Empty;

        public byte MyId { get; set; } = 0;
        public byte PartnerId { get; set; } = 0;

        public delegate void ReceivedEventHandler(object sender, EventArgs e);
        public delegate void ViewDebugDelegate(IEnumerable<byte> bytes);

        private ViewDebugDelegate _viewDebugDelegate;

        public SerialPortCommunicator(ViewDebugDelegate viewDebugDelegate)
        {
            _viewDebugDelegate = viewDebugDelegate;
        }

        public bool IsOpen => _serialPort != null;

        /// <summary>
        /// Sending info to serial port
        /// </summary>
        /// <param name="s">Sending info</param>
        public void Send(string s)
        {
            InternalLogger.Log.Debug($"Sending string: \"{s}\"");

            foreach (byte[] bytes in SplitToPackets(s))
            {
                var dataToSend = GeneratePacket(bytes).ToArray();
                _serialPort.Write(dataToSend, 0, dataToSend.Length);
            }
        }

        /// <summary>
        /// Closing current connection
        /// </summary>
        public void Close()
        {
            if (!IsOpen) return;
            _serialPort.Close();
            _serialPort = null;
        }

        /// <summary>
        /// Open port with settings
        /// </summary>
        /// <param name="portName">Port name</param>
        /// <param name="baudRate">Baudrate</param>
        /// <param name="parity">Parity</param>
        /// <param name="dataBits">Number of data bits</param>
        /// <param name="stopBits">Number of stop bits</param>
        /// <param name="receivedEventHandler">Callback to run to when something is received</param>
        public void Open(string portName, EBaudrate baudRate, Parity parity, EDataBits dataBits, StopBits stopBits,
            ReceivedEventHandler receivedEventHandler)
        {
            if (IsOpen) return;
            _serialPort = new SerialPort(portName, (int)baudRate, parity, (int)dataBits, stopBits);
            _serialPort.Open();
            if (receivedEventHandler != null)
                _serialPort.DataReceived +=
                    delegate (object sender, SerialDataReceivedEventArgs args) { receivedEventHandler(sender, args); };
            _receivedBuffer = string.Empty;
        }

        /// <summary>
        /// Read existing string
        /// </summary>
        /// <returns>Existing string</returns>
        public string ReadExisting()
        {
            //Reading data from serial port
            while (_serialPort.BytesToRead > 0)
            {
                var received = _serialPort.ReadByte();
                if (received < 0)
                {
                    InternalLogger.Log.Info("End of the stream was read");
                    break;
                }
                _receivedBuffer += (BytesToBools(new[] { (byte)received }));
            }

            _viewDebugDelegate?.Invoke(BoolsToBytes(_receivedBuffer));

            IEnumerable<byte> data = null;
            try
            {
                data = ParsePacket(_receivedBuffer, out int index);
                _receivedBuffer = _receivedBuffer.Remove(0, index);
            }
            catch (CannotFindStopSymbolException)
            {
                //All is good
                return "";
            }
            catch (CannotFindStartSymbolException)
            {
                _receivedBuffer = string.Empty;
                throw;
            }
            return data != null ? Encoding.UTF8.GetString(data.ToArray()) : "";
        }

        /// <summary>
        /// Encode byte array to bit array with adding bit staffing
        /// </summary>
        /// <param name="inputBytes">Bytes to convert with bit staffing</param>
        /// <returns>Converted input value</returns>
        private string Encode(IEnumerable<byte> inputBytes)
        {
            var res = BytesToBools(inputBytes);

            res = res.Replace(BitStaffingSeqBeforeStaffing, BitStaffingSeqAfterStaffing);

            return res;
        }

        /// <summary>
        /// Decode bit array by deletinig bit staffing
        /// </summary>
        /// <param name="inputBits">Bits to decode with bit staffing</param>
        /// <param name="index">Index of first non-used element (number of used bits)</param>
        /// <returns>Decoded input value</returns>
        private string Decode(string inputBits, out int index)
        {
            var res = inputBits;

            index = inputBits.Length;
            res = res.Replace(BitStaffingSeqAfterStaffing, BitStaffingSeqBeforeStaffing);

            return res;
        }

        /// <summary>
        /// Check is packet to me
        /// </summary>
        /// <param name="packet">Packet to check</param>
        /// <returns>True if packet addressed to me, else false</returns>
        private bool IsPacketToMe(IEnumerable<byte> packet)
        {
            return (packet.First() == MyId);
        }

        /// <summary>
        /// Delete address metadata from data
        /// </summary>
        /// <param name="data">Data to delete address metadata</param>
        /// <returns>String without address metadata if data adressed to myId, else empty array</returns>
        private IEnumerable<byte> DeleteAddressMetadata(IEnumerable<byte> data)
        {
            var modifiedData = data.ToList();
            var b = modifiedData.Aggregate<byte, byte>(0, (current, b1) => (byte)(current ^ b1));
            if (b != 0 || !IsPacketToMe(modifiedData))
            {
                return null;
            }

            modifiedData.RemoveAt(modifiedData.Count - 1);
            modifiedData.RemoveRange(0, 2);
            return modifiedData;
        }

        /// <summary>
        /// Wrap address metadata to data
        /// </summary>
        /// <param name="data">Data to wrap</param>
        /// <returns>Wrapped string</returns>
        private IEnumerable<byte> WrapAddressMetadata(IEnumerable<byte> data)
        {
            var modifiedData = data.ToList();

            modifiedData.Insert(0, MyId);
            modifiedData.Insert(0, PartnerId);

            return modifiedData;
        }

        /// <summary>
        /// Generate packet from data
        /// </summary>
        /// <param name="data">Data to add to packet</param>
        /// <returns>Generated packet</returns>
        private IEnumerable<byte> GeneratePacket(IEnumerable<byte> data)
        {
            List<byte> package = WrapAddressMetadata(data).ToList();
            package.Insert(0, StartStopByte);

            byte b = CalculateFcs(package);
            package.Add(b);
            InternalLogger.Log.Debug($"FCS value: {b}");

            return package;
        }

        /// <summary>
        /// Calculate Fcs info
        /// </summary>
        /// <param name="package">Package to calculate FCS</param>
        /// <returns></returns>
        private byte CalculateFcs(IEnumerable<byte> package)
        {
            return package.Aggregate<byte, byte>(0, (current, b1) => (byte) (current ^ b1));
        }

        /// <summary>
        /// Parse packet
        /// </summary>
        /// <param name="packet">Packet to parse</param>
        /// <param name="index">Index of first unused bool element in packet</param>
        /// <returns>Data from packet if packet addressed to me, else return null</returns>
        private IEnumerable<byte> ParsePacket(string packet, out int index)
        {
            index = packet.IndexOf(StartStopByte, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new CannotFindStartSymbolException();
            }

            index += BitsInByte;
            packet = packet.Remove(0, index);

            int indexof = packet.IndexOf(StartStopByte, StringComparison.Ordinal);

            string decoded = Decode(packet.Substring(0, indexof < 0 ? packet.Length : indexof), out var decodeIndex);

            if (string.IsNullOrEmpty(decoded))
            {
                index = 0;
                return new List<byte>();
            }

            var res = DeleteAddressMetadata(BoolsToBytes(decoded))?.ToList();
            if (res == null)
            {
                index = 0;
                return new List<byte>();
            }

            index += decodeIndex;
            res.RemoveRange(res[0] + 1, res.Count - res[0] - 1);
            res.RemoveAt(0);
            return res;
        }

        /// <summary>
        /// Encode & split message to packets. Need to add address, FCS & start bit to sending
        /// </summary>
        /// <param name="message">Message to send</param>
        /// <returns></returns>
        private IEnumerable<byte[]> SplitToPackets(string message)
        {
            string encoded = Encode(Encoding.UTF8.GetBytes(message));
            return Enumerable
                .Range(0, (encoded.Length + DataInPacketSizeInBytes - 1) / DataInPacketSizeInBytes)
                .Select(i => i * DataInPacketSizeInBytes)
                .Select(i => encoded.Substring(i, Math.Min(encoded.Length - i, DataInPacketSizeInBytes)))
                .Select(s => s.Length != (DataInPacketSizeInBytes * BitsInByte) ? s + new string('0', DataInPacketSizeInBytes * BitsInByte - s.Length) : s)
                .Select(s => BoolsToBytes(s).ToArray());
        }

        /// <summary>
        /// Converts byte array to bool (bit) array 
        /// </summary>
        /// <param name="data">Data to convert</param>
        /// <returns>Bit array</returns>
        private string BytesToBools(IEnumerable<byte> data)
        {
            var bitArray = new BitArray(data.ToArray());
            string res = string.Empty;

            foreach (bool b in bitArray)
            {
                res += b ? "1" : "0";
            }

            return res;
        }

        /// <summary>
        /// Converts bool (bit) array to byte array 
        /// </summary>
        /// <param name="data">Data to convert</param>
        /// <returns>Byte array</returns>
        private IEnumerable<byte> BoolsToBytes(string data)
        {
            var bitArray = new BitArray(data.Length, false);

            for (int i = 0; i < data.Length; ++i)
            {
                bitArray[i] = (data[i] == '1');
            }

            var bytes = new byte[(bitArray.Length + BitsInByte - 1) / BitsInByte];
            bitArray.CopyTo(bytes, 0);
            return bytes;
        }
    }
}