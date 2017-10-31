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

        private const byte BitStaffingCheckMask = 0x54;
        private const byte BitStaffingAndMask = 0xFE;
        // not 0x7F, because start parsing (receiving) data from low-order bit
        private const byte BitStaffingReplaceSymbol = 0xD4;
        private const byte GetLastBitMask = 0x01;
        private const int DataInPacketSizeInBytes = 15;
        private const int PacketSizeInBytesWithoutStartByte = DataInPacketSizeInBytes + 4;
        private const byte EmptyByte = 0x00;
        private readonly List<bool> _receivedBuffer = new List<bool>();

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

            while (!string.IsNullOrEmpty(s))
            {
                var packet = new List<byte>();
                var i = 1;
                var olds = s;
                for (; i <= olds.Length; i++)
                {
                    var maybePacket = Encoding.UTF8.GetBytes(s.Substring(0, i));
                    if (maybePacket.Length > DataInPacketSizeInBytes)
                    {
                        s = olds.Substring(i - 1);
                        break;
                    }

                    packet = maybePacket.ToList();
                }

                if (i > olds.Length)
                {
                    s = null;
                }

                var dataToSend = GeneratePacket(packet).ToArray();
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
            _serialPort = new SerialPort(portName, (int) baudRate, parity, (int) dataBits, stopBits);
            _serialPort.Open();
            if (receivedEventHandler != null)
                _serialPort.DataReceived +=
                    delegate(object sender, SerialDataReceivedEventArgs args) { receivedEventHandler(sender, args); };
            _receivedBuffer.Clear();
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
                _receivedBuffer.AddRange(BytesToBools(new[] {(byte) received}));
            }

            _viewDebugDelegate?.Invoke(BoolsToBytes(_receivedBuffer));

            IEnumerable<byte> data = null;
            try
            {
                data = ParsePacket(_receivedBuffer, out int index);
                _receivedBuffer.RemoveRange(0, index - 1);
            }
            catch (CannotFindStopSymbolException)
            {
                //All is good
                return "";
            }
            catch (CannotFindStartSymbolException)
            {
                _receivedBuffer.Clear();
                throw;
            }
            return data != null ? Encoding.UTF8.GetString(data.ToArray()) : "";
        }

        /// <summary>
        /// Encode byte array to bit array with adding bit staffing
        /// </summary>
        /// <param name="inputBytes">Bytes to convert with bit staffing</param>
        /// <returns>Converted input value</returns>
        private IEnumerable<bool> Encode(IEnumerable<byte> inputBytes)
        {
            var res = BytesToBools(inputBytes).ToList();

            for (var i = 0; i < (res.Count - BitsInByte + 1); ++i)
            {
                var b = BoolsToBytes(res.GetRange(i, BitsInByte)).First();
                var isFindByteToStuffing = (((b & BitStaffingAndMask) ^ BitStaffingCheckMask) == 0);

                if (!isFindByteToStuffing) continue;

                //Skipping staffed 8 bits
                i += BitsInByte - 1;
                res.Insert(i, true);
            }

            return res;
        }

        /// <summary>
        /// Decode bit array by deletinig bit staffing
        /// </summary>
        /// <param name="inputBits">Bits to decode with bit staffing</param>
        /// <param name="index">Index of first non-used element (number of used bits)</param>
        /// <returns>Decoded input value</returns>
        private IEnumerable<bool> Decode(IEnumerable<bool> inputBits, out int index)
        {
            var res = inputBits.ToList();

            for (index = 0; index < (res.Count - BitsInByte + 1); ++index)
            {
                var b = BoolsToBytes(res.GetRange(index, BitsInByte)).First();

                /*if (b == StartStopByte)
                {
                    //removing all to the end from stop symbol
                    res.RemoveRange(index, res.Count - index);
                    index += BitsInByte;
                    return res;
                }*/
                if (b == BitStaffingReplaceSymbol)
                {
                    //Skipping staffed 8 bits (will be 7 after deleting bit)
                    index += BitsInByte - 2;
                    res.RemoveAt(index + 1);
                }
            }

            index += BitsInByte;
            return res;

            //throw new CannotFindStopSymbolException();
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
            var b = modifiedData.Aggregate<byte, byte>(0, (current, b1) => (byte) (current ^ b1));
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

            var b = modifiedData.Aggregate<byte, byte>(0, (current, b1) => (byte) (current ^ b1));
            modifiedData.Add(b);

            return modifiedData;
        }

        /// <summary>
        /// Generate packet from data
        /// </summary>
        /// <param name="data">Data to add to packet</param>
        /// <returns>Generated packet</returns>
        private IEnumerable<byte> GeneratePacket(IEnumerable<byte> data)
        {
            var addedData = data.ToList();
            // inserting size of data
            addedData.Insert(0, (byte) addedData.Count);

            while (addedData.Count < (DataInPacketSizeInBytes + 1))
            {
                addedData.Add(EmptyByte);
            }

            var encodedMeta = Encode(WrapAddressMetadata(addedData)).ToList();
            encodedMeta.InsertRange(0, BytesToBools(new[] {StartStopByte}));
            return BoolsToBytes(encodedMeta);
        }

        /// <summary>
        /// Parse packet
        /// </summary>
        /// <param name="packet">Packet to parse</param>
        /// <param name="index">Index of first unused bool element in packet</param>
        /// <returns>Data from packet if packet addressed to me, else return null</returns>
        private IEnumerable<byte> ParsePacket(IEnumerable<bool> packet, out int index)
        {
            var listedPackage = packet.ToList();
            index = 0;
            var isStartFound = false;
            while (index < (listedPackage.Count - BitsInByte + 1))
            {
                if (BoolsToBytes(listedPackage.GetRange(index, BitsInByte)).ToArray()[0] == StartStopByte)
                {
                    isStartFound = true;
                    break;
                }
                index++;
            }
            if (!isStartFound)
            {
                throw new CannotFindStartSymbolException();
            }

            index += BitsInByte;
            listedPackage.RemoveRange(0, index);

            int decodeIndex = 0;
            List<bool> decoded = null;
            for (var i = PacketSizeInBytesWithoutStartByte * BitsInByte; i <= listedPackage.Count; i++)
            {
                var decodedInternal = Decode(listedPackage.GetRange(0, i), out decodeIndex).ToList();
                if (PacketSizeInBytesWithoutStartByte * BitsInByte < decodedInternal.Count) break;
                decoded = decodedInternal;
            }

            if (decoded == null)
            {
                index = 0;
                return new List<byte>();
            }

            var res = DeleteAddressMetadata(BoolsToBytes(decoded)).ToList();
            index += decodeIndex;
            res.RemoveRange(res[0] + 1, res.Count - res[0] - 1);
            res.RemoveAt(0);
            return res;
        }

        /// <summary>
        /// Converts byte array to bool (bit) array 
        /// </summary>
        /// <param name="data">Data to convert</param>
        /// <returns>Bit array</returns>
        private IEnumerable<bool> BytesToBools(IEnumerable<byte> data)
        {
            var bitArray = new BitArray(data.ToArray());
            var bits = new bool[bitArray.Length];
            bitArray.CopyTo(bits, 0);
            return bits;
        }

        /// <summary>
        /// Converts bool (bit) array to byte array 
        /// </summary>
        /// <param name="data">Data to convert</param>
        /// <returns>Byte array</returns>
        private IEnumerable<byte> BoolsToBytes(IEnumerable<bool> data)
        {
            var bitArray = new BitArray(data.ToArray());
            var bytes = new byte[(bitArray.Length + BitsInByte - 1) / BitsInByte];
            bitArray.CopyTo(bytes, 0);
            return bytes;
        }
    }
}