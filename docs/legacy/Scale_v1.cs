// Original first version of the serial-protocol algorithm (v1), kept unchanged for comparison
// with the rewrite in src/. It is not part of the build. See README "v1 → v2" for the list of
// issues this version had and how v2 addresses them.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Scale
{
    public class Scale
    {
        #region Variables
        private volatile bool _shouldStop;
        bool checkport = false;
        int mBaudRate = 9600;
        int mParity = 0;
        int mDataBits = 8;
        int mStopBits = 1;
        bool isDistance = false;
        SerialPort serialport;
        byte SOST;
        byte oldSOST = 0;
        byte SOST9;
        byte[] buffer1 = { };

        bool[] Sensor = new bool[2] { true, false };
        bool[] IR = new bool[2] { true, false };
        bool[] InductionSensor = new bool[2] { true, false };
        bool[] TireSensor = new bool[2] { true, false };
        bool[] CommSensor = new bool[2] { true, false };
        #endregion

        public void DoWork()
        {
            while (!_shouldStop)
            {
                try
                {
                    if (!checkport || !serialport.IsOpen)
                        CheckComPortList();
                    if (!serialport.IsOpen)
                    {
                        SetSensorStatus(255);
                        Thread.Sleep(5000);
                        continue;
                    }

                    int yu = 0;

                rep1: //msg += _port.ReadExisting();
                    int bytes = serialport.BytesToRead;
                    if (bytes == 0 && buffer1.Count() == 0)
                    {
                        yu++;
                        Thread.Sleep(1000);
                    }
                    if (yu > 6)
                    {
                        //checkport = false;
                        if (_shouldStop) break;
                        //throw new NotImplementedException();
                    }
                    byte[] buffer = new byte[bytes];
                    serialport.Read(buffer, 0, bytes);
                    AppendSpecifiedBytes(ref buffer1, buffer);
                    //setlabel(0, buffer1.Count());
                    int pos1 = Array.IndexOf(buffer1, (byte)255);       //"\x2"
                    int pos2;
                    if (pos1 == -1)
                        goto rep1;
                    byte length = buffer1[pos1 + 3];
                    if (length == bytes || length == buffer1.Count())
                        pos2 = length + pos1;
                    else
                    {
                        pos2 = Array.IndexOf(buffer1, (byte)255, pos1 + 1);                 //"\x3"
                        if (pos2 == -1)
                        {
                            Thread.Sleep(1000);
                            goto rep1;
                        }
                    }
                    if (length == pos2 - pos1)
                    {
                        byte command = buffer1[pos1 + 2];
                        //setlabel(command, buffer1.Count());
                        switch (command)
                        {
                            case 5:
                                SOST = (byte)buffer1[pos1 + 4];
                                SetSensorStatus(SOST);
                                break;
                            case 1:
                                LoadWeightDataFromBytes(buffer1, pos1);
                                SetSensorStatus(SOST);
                                break;
                            case 6:
                                break;
                            case 9:
                                SOST9 = (byte)buffer1[pos1 + 4];
                                break;
                            default:
                                Thread.Sleep(2000);
                                goto rep1;
                        }
                        buffer1 = Remove(buffer1, 0, pos2);
                    }
                }
                catch
                {
                    SetSensorStatus(255);
                    checkport = false;
                    Thread.Sleep(2000);
                }
                if (buffer1.Count() == 0)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        private void SetSensorStatus(byte SOST)
        {
            if (SOST != oldSOST)
            {
                bool img1 = Sensor[SOST & 1];
                bool img2 = IR[(SOST & 2) >> 1];
                bool img3 = InductionSensor[(SOST & 4) >> 2];
                bool img4 = TireSensor[(SOST & 8) >> 3];
                bool img5 = CommSensor[(SOST & 16) >> 4];
                oldSOST = SOST;
                //SendWeightStatus(img1, img2, img3, img4, img5);
            }
        }

        private void CheckComPortList()
        {
            SerialPort port = null;
            port = new SerialPort();
            port.BaudRate = mBaudRate;
            port.Parity = (Parity)mParity;
            port.DataBits = mDataBits;
            port.StopBits = (StopBits)mStopBits;
            // string msg = "";
            try
            {
                if (serialport != null && serialport.IsOpen)
                    serialport.Close();

                string[] ports = SerialPort.GetPortNames();
                for (int i = 0; i < ports.Length; i++)
                {
                got:
                    int stp = 0;
                    try
                    {
                        if (port.IsOpen) port.Close();
                        port.PortName = ports[i];
                        if (!port.IsOpen)
                        {
                            port.Open();
                        }
                        Thread.Sleep(1000);
                        int koll = 0;

                    rep1: //msg += port.ReadExisting();
                          //    int pos1 = msg.IndexOf((char)255);    //"\x2"
                        int bytes = 0;

                        for (int yu = 0; yu < 50 && bytes == 0; yu++)
                        {
                            bytes = port.BytesToRead;
                            Thread.Sleep(100);
                        }

                        if (bytes == 0) continue;

                        byte[] buffer = new byte[bytes];
                        port.Read(buffer, 0, bytes);
                        int pos1 = Array.IndexOf(buffer, (byte)255);
                        if (pos1 == -1)
                        {
                            koll++;
                            if (koll > 5)
                            {
                                Thread.Sleep(1000);
                                continue;
                            }
                            goto rep1;
                        }
                        int pos2;
                        byte length = buffer[pos1 + 3];
                        if (length == bytes || length == buffer.Count())
                            pos2 = length + pos1;
                        else
                        {
                            pos2 = Array.IndexOf(buffer, (byte)255, pos1 + 1);
                            if (pos2 == -1)
                            {
                                koll++;
                                if (koll > 10)
                                {
                                    Thread.Sleep(1000);
                                    continue;
                                }
                                goto rep1;
                            }
                        }
                        if (length == pos2 - pos1)
                        {
                        //yes:
                            if (port.IsOpen)
                                port.Close();
                            serialport.PortName = ports[i];
                            serialport.Open();
                            checkport = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        //Utils.Log("checkport: " + ex.Message);
                        checkport = false;
                        Thread.Sleep(1000);
                        i++;
                        if (i < ports.Length)
                            goto got;
                    }
                }
            }
            catch (Exception ex)
            {
                checkport = false;
                //Utils.Log("checkport = false: " + ex.Message);
            }
            finally
            {
                if (port.IsOpen)
                    port.Close();
            }
            port.Dispose();
        }

        private byte[] Remove(byte[] items, int startIndex, int count)
        {
            List<byte> tmp = items.ToList();
            tmp.RemoveRange(startIndex, count);
            return tmp.ToArray();
        }

        private void AppendSpecifiedBytes(ref byte[] dst, byte[] src)
        {
            try
            {
                // Get the starting length of dst
                int i = dst.Length;
                // Resize dst so it can hold the bytes in src
                Array.Resize(ref dst, dst.Length + src.Length);
                // For each element in src
                for (int j = 0; j < src.Length; j++)
                {
                    // Add the element to dst
                    dst[i] = src[j];
                    // Increment dst index
                    i++;
                }
            }
            catch { }
        }

        private void LoadWeightDataFromBytes(byte[] WeightData, int Position)
        {
            try
            {
                #region Load Weight Data From Bytes
                WeighingResult data = new WeighingResult();
                //byte[] tmp = Encoding.ASCII.GetBytes(msg);
                data.WeightDate = new DateTime((int)WeightData[Position + 4] * 256 + (int)WeightData[Position + 5], (int)WeightData[Position + 6], (int)WeightData[Position + 7], (int)WeightData[Position + 8], (int)WeightData[Position + 9], (int)WeightData[Position + 10]);
                SOST = WeightData[Position + 11];
                data.Speed = (decimal)((int)WeightData[Position + 12] * 256 + (int)WeightData[Position + 13]) / 10;
                data.Acceleration = (int)WeightData[Position + 14];
                data.NumberOfAxles = WeightData[Position + 15];
                data.NumberOfAxlesGroup = WeightData[Position + 16];
                data.AxlesWeight = null;
                data.AxlesWeight = new int[data.NumberOfAxles];
                int ppos = Position + 17;
                data.FullWeight = 0;
                for (int i = 0; i < data.NumberOfAxles; i++)
                {
                    data.AxlesWeight[i] = WeightData[ppos + i * 2] * 256 + WeightData[ppos + i * 2 + 1];
                    data.FullWeight += data.AxlesWeight[i];
                }
                ppos += (data.NumberOfAxles) * 2;
                data.AxlesGroupWeight = null;
                data.AxlesGroupWeight = new int[data.NumberOfAxlesGroup];
                for (int i = 0; i < data.NumberOfAxlesGroup; i++)
                {
                    data.AxlesGroupWeight[i] = WeightData[ppos + i * 2] * 256 + WeightData[ppos + i * 2 + 1];
                }
                ppos += (data.NumberOfAxlesGroup) * 2;
                data.AxlesGroupTypes = null;
                data.AxlesGroupTypes = new int[data.NumberOfAxlesGroup];
                for (int i = 0; i < data.NumberOfAxlesGroup; i++)
                {
                    data.AxlesGroupTypes[i] = WeightData[ppos + i];
                }
                ppos += data.NumberOfAxlesGroup;
                data.Distances = null;
                data.Distances = new int[data.NumberOfAxles - 1];
                for (int i = 0; i < data.NumberOfAxles - 1; i++)
                {
                    data.Distances[i] = isDistance ? (WeightData[ppos + i * 2] * 256 + WeightData[ppos + i * 2 + 1]) : 0;
                }
                SendSuccessToWeight(1, 0);
                #endregion

                //ProcessWeightData(data);
            }
            catch (Exception ex)
            {
                SendSuccessToWeight(1, 1);
                Utils.Log("LoadData 27: " + ex.Message);
            }
        }

        private void SendSuccessToWeight(byte command, byte success = 0)
        {
            try
            {
                byte[] arr = { 255, 0, command, success, 0, 0 };
                byte[] gn = BitConverter.GetBytes(calcCrc(arr, 4));
                arr[4] = gn[0];
                arr[5] = gn[1];
                serialport.Write(arr, 0, 6);
            }
            catch (Exception ex)
            {
                Utils.Log("SendToTraroziSuccess: " + ex.Message);
            }
        }

        public ushort calcCrc(byte[] data, int ln)
        {
            unchecked
            {
                ushort crc = 0;

                for (int a = 0; a < ln; a++)
                {
                    crc ^= (ushort)(data[a] << 8);
                    for (int i = 0; i < 8; i++)
                    {
                        if ((crc & 0x8000) != 0)
                            crc = (ushort)((crc << 1) ^ 0x1021);
                        else
                            crc = (ushort)(crc << 1);
                    }
                }
                //byte upper = Convert.ToByte(crc >> 8);
                //byte lower = ;
                crc = Convert.ToUInt16((crc >> 8) | (crc & 0x00FF) << 8);
                return crc;
            }
        }
    }
}
