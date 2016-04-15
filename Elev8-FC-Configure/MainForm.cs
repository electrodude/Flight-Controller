﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Threading;

using FTD2XX_NET;

namespace Elev8
{
    public partial class MainForm : Form
    {
		const int OneG = 4096;

		bool Active = false;

		FTDI ftdi = null;

		enum CommStatus
		{
			Initializing,
			NoDevices,
			NoElev8,
			Connected,
		}

		public struct EVERYTHING_DATA
		{
			public short Thro, Aile, Elev, Rudd, Gear, Aux1, Aux2, Aux3;         // Radio values = 16 bytes
			public short BatteryVolts;                                           // Battery Monitor = 2 bytes
			public short padding;                                                // curently unused
			public short Temp, GyroX, GyroY, GyroZ, AccelX, AccelY, AccelZ, MagX, MagY, MagZ;  // IMU sensors = 20 bytes
			public long  Alt, AltRate, Pressure;                                 // Altimeter = 12 bytes
			public long  Pitch, Roll, Yaw;                                       // IMU = 12 bytes

			public Quaternion q;												// Quaternion = 16 bytes
		};                                                                      // 80 bytes total

		EVERYTHING_DATA Everything = new EVERYTHING_DATA();


		int Thro = 0;
		int Aile = 0;
		int Elev = 0;
		int Rudd = 0;
		int Gear = 0;
		int Aux1 = 0;
		int Aux2 = 0;
		int Aux3 = 0;

		int GyroTemp;
		int GyroX, GyroY, GyroZ;
		int AccelX, AccelY, AccelZ;
		int MagX, MagY, MagZ;
		int Alt, AltTemp, AltEst, prevAlt = 0;
		int Battery = 0;
		int Pitch, Roll, Yaw;

		float[] accXCal = new float[4];
		float[] accYCal = new float[4];
		float[] accZCal = new float[4];


		byte[] txBuffer = new byte[32];
		byte[] rxBuffer = new byte[256];

		const int QSize = 4096;
		byte[] rxQueue = new byte[QSize];
		int QHead = 0;
		int QTail = 0;

		byte[] OutputMode = { 0, 1, 2, 3, 2, 2, 4, 5, 6, 7 };			// None=0, Radio=1, Sensors=2, Motor=3, Sensors=2, IMU=4, IMUComp=5, VibeTest=6, Everything=7
		byte[] PacketSizes = { 0, 16+3, 34+3, 0, 22+3, 16+3, 3+6, 4+20 };	// None=0, Radio=19, Sensors=35, Motor=0, IMU=25, IMUComp=16, VibeTest=6 (bytes), Everything=24 bytes per phase
		int SampleCounter = 0;

		int[] AltTable = new int[251];

		Vector velocityEstimate = new Vector( 0.0f, 0.0f, 0.0f );
		Vector positionEstimate = new Vector( 0.0f, 0.0f, 0.0f );
		float altVelocity = 0.0f;

		CommStatus commStat = CommStatus.NoDevices;
		CommStatus lastStat = CommStatus.Initializing;


		enum Mode
		{
			None,
			RadioTest,
			SensorTest,
			MotorTest,
			GyroCalibration,
			AccelCalibration,
			IMUTest,
			IMUCompare,
			VibrationTest,
			Everything,
		};

		Mode currentMode = Mode.None;
		int CalibrationCycle = 0;



		public MainForm()
        {
            InitializeComponent();
			Everything.q = new Quaternion();

			gGyroTemp.displayOffset = 25.0f;
			gGyroTemp.displayScale = 1.0f / 16.0f;	// 16 deg C per bit
			gGyroTemp.displayPostfix = " *C";

			gHeading.displayScale = 0.1f;	// We're going to feed it +/- 1800 units, or 10 units per degree
			gHeading.GaugeCircle = 1.0f;	// want this to use the full 360 degrees of the gauge, unlike a normal analog gauge

			gPitch.displayScale = 180.0f / 65536.0f;	// Convert -65536 to +63356 to -180 to +180 degrees
			gRoll.displayScale = 180.0f / 65536.0f;
			gYaw.displayScale = 180.0f / 32768.0f;		// Heading is -32768 to +32768

			ConnectFTDI();

			GenerateAltitudeTable();
        }


		void UpdateCommStatus()
		{
			if(commStat != lastStat)
			{
				string msg = null;

				lastStat = commStat;
				switch(commStat)
				{
					case CommStatus.Initializing:
						msg = "Initializing";
						break;

					case CommStatus.NoDevices:
						msg = "No FTDI devices found";
						break;

					case CommStatus.NoElev8:
						msg = "Looking for Elev8-FC";
						break;

					case CommStatus.Connected:
						msg = "Connected";
						break;
				}

				tsStatLabel.Text = msg;

				if(commStat == CommStatus.Connected)
				{
					tickTimer.Interval = 20;
				}
				else
				{
					tickTimer.Interval = 100;
				}
			}
		}


		void ConnectFTDI()
		{
			UInt32 DeviceCount = 0;
			FTDI.FT_STATUS ftStatus = FTDI.FT_STATUS.FT_OK;

			// Create new instance of the FTDI device class
			ftdi = new FTDI();

			// Determine the number of FTDI devices connected to the machine
			ftStatus = ftdi.GetNumberOfDevices( ref DeviceCount );

			// Check status
			if(ftStatus != FTDI.FT_STATUS.FT_OK || DeviceCount == 0) {
				commStat = CommStatus.NoDevices;
				return;
			}

			commStat = CommStatus.NoElev8;

			// Allocate storage for device info list
			FTDI.FT_DEVICE_INFO_NODE[] DeviceList = new FTDI.FT_DEVICE_INFO_NODE[DeviceCount];

			try
			{
				// Populate our device list
				ftStatus = ftdi.GetDeviceList( DeviceList );

				bool FoundElev8 = false;
				for(int i = 0; i < DeviceCount && FoundElev8 == false; i++)
				{
					if(DeviceList[i].Type != FTDI.FT_DEVICE.FT_DEVICE_X_SERIES) continue;

					ftStatus = ftdi.OpenBySerialNumber( DeviceList[i].SerialNumber );
					if(ftStatus == FTDI.FT_STATUS.FT_OK)
					{
						string portName;
						ftdi.GetCOMPort( out portName );
						if(portName == null || portName == "")
						{
							ftdi.Close();
							continue;
						}

						ftdi.SetBaudRate( 115200 );
						ftdi.SetDataCharacteristics( 8, 1, 0 );
						ftdi.SetFlowControl( FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0, 0 );


						txBuffer[0] = (byte)0xff;	// Send 0xff to the Prop to see if it replies
						uint written = 0;

						for(int j = 0; j < 10 && FoundElev8 == false; j++)	// Keep pinging until it replies, or we give up
						{
							ftdi.Write( txBuffer, 1, ref written );
							System.Threading.Thread.Sleep( 50 );

							uint bytesAvail = 0;
							ftdi.GetRxBytesAvailable( ref bytesAvail );				// How much data is available from the serial port?
							if(bytesAvail > 0)
							{
								uint bytesRead = 0;
								ftdi.Read( rxBuffer, 1, ref bytesRead );			// If it comes back with 0xE8 it's the one we want
								if(bytesRead == 1 && rxBuffer[0] == 0xE8)
								{
									FoundElev8 = true;
									commStat = CommStatus.Connected;
									break;
								}
							}
						}
						if(FoundElev8)
						{
							break;
						}
						else
						{
							ftdi.Close();
						}
					}
				}
			}

			catch(Exception) {
				return;
			}


			Active = true;
			if( ftdi.IsOpen ) {
				currentMode = (Mode)(tcMainTabs.SelectedIndex + 1);
				txBuffer[0] = (byte)currentMode;
				uint written = 0;
				ftdi.Write( txBuffer, 1, ref written );	// Which mode are we in?
			}

			// Start my 'tick timer' - It's set to tick every 20 milliseconds
			// (used to check the comm port periodically instead of using a thread)
			//tickTimer.Start();
		}


		private void SetActive( bool NewActive )
		{
			if(NewActive == true && (ftdi == null || ftdi.IsOpen == false)) {
				ConnectFTDI();
			}

			if(Active == NewActive) return;
			if(NewActive == false) {

				currentMode = Mode.None;
				if(ftdi != null && ftdi.IsOpen)
				{
					uint written = 0;
					txBuffer[0] = OutputMode[ (byte)currentMode ];
					ftdi.Write( txBuffer, 1, ref written );	// Which mode are we in?

					ftdi.Close();
				}
			}
			else {
				ConnectFTDI();
			}

			Active = NewActive;
		}

		private void MainForm_Activated( object sender, EventArgs e )
		{
			SetActive( true );
		}

		private void MainForm_Deactivate( object sender, EventArgs e )
		{
			SetActive( false );
		}


		byte GetCommByte()
		{
			return rxQueue[QTail++];
		}

		short GetCommShort()
		{
			short val = (short)BitConverter.ToInt16( rxQueue, QTail );
			QTail += 2;

			return val;
		}

		int GetCommWord()
		{
			int val = (int)BitConverter.ToInt16( rxQueue, QTail );
			QTail += 2;

			return val;
		}

		int GetCommTriple()
		{
			int val = (rxQueue[QTail++] << 16);
			val |= (rxQueue[QTail++] << 8);
			val |= rxQueue[QTail++];
			return val;
		}


		float GetCommFloat()
		{
			float Result = BitConverter.ToSingle( rxQueue, QTail );
			QTail += 4;
			return Result;
		}

		int GetCommLong()
		{
			int Result = BitConverter.ToInt32( rxQueue, QTail );
			QTail += 4;
			return Result;
		}


#if NEW_COMM_THREAD

		volatile bool TerminateConnection = false;


		private void CommThread()
		{
			while( !TerminateConnection )
			{
				if(Active == false || ftdi == null)
				{
					Thread.Sleep( 20 );
					continue;
				}

				if(ftdi.IsOpen == false)
				{
					ConnectFTDI();

					if(ftdi.IsOpen == false) {
						Thread.Sleep( 20 );
						continue;
					}
				}

				// Processing goes here
					// Pull data from the comm port
					// add it to a queue
					// process when there's enough data
					// notify the main thread that something happened
			}
		}
#endif



		private void tickTimer_Tick( object sender, EventArgs e )
		{
			UpdateCommStatus();
			if(Active == false || ftdi == null) return;

			if(ftdi.IsOpen == false) {
				ConnectFTDI();

				if( ftdi.IsOpen == false ) return;
			}

			try
			{
				uint QAvail = (uint)(QSize - QHead);					// How much room is available at the end of the data queue?
				uint bytesAvail = 0;
				FTDI.FT_STATUS stat = ftdi.GetRxBytesAvailable( ref bytesAvail );				// How much data is available from the serial port?
				if(stat == FTDI.FT_STATUS.FT_IO_ERROR) {
					// If we got an error, the port has likely been closed / unplugged - go back to waiting
					ftdi.Close();
					return;
				}

				if(bytesAvail > QAvail) bytesAvail = QAvail;			// Pick the smaller of the two values for how much to read

				while( bytesAvail > 0 && QHead < (rxQueue.Length - rxBuffer.Length) )
				{
					uint toRead = Math.Min( (uint)bytesAvail , (uint)rxBuffer.Length );
					uint bytesRead = 0;
					ftdi.Read( rxBuffer, toRead, ref bytesRead );
					rxBuffer.CopyTo( rxQueue, QHead );
					QHead += (int)bytesRead;
					bytesAvail -= bytesRead;
					//QHead += comm.Read( rxQueue, QHead, bytesAvail );	// Read from the serial port into the data queue buffer
				}

				do {
					if( QTail < (QHead-3) ) {							// Keep going as long as we can figure out what the next packet type should be
						if( rxQueue[QTail] != 0x77 )					// Check for our two signature bytes (0x77, 0x77)
							QTail++;
						else if( rxQueue[QTail+1] != 0x77 )				// If we don't see them, just consume data until we do
							QTail++;
						else
						{
							byte packetType = rxQueue[QTail + 2];		// Packet type is the 3rd byte
							if( packetType > (byte)Mode.Everything )	// If it's out of range, this is a bad packet, so skip what we've read and move on
								QTail += 3;
							else {
								byte packetSize = PacketSizes[ packetType ];	// Figure out how big the packet size is
								if((QHead - QTail) >= packetSize)		// Have we got enough data to process the packet?
								{
									ProcessPacket( packetType );		// Yep - do it
								}
								else
									break;	// Get out of the loop because we're out of data and need more
							}
						}
					}
				} while( QTail < (QHead-3) );	// Keep going until we're out of data


				// Move any data we have remaining in the queue back to the beginning.  This means we don't
				// have to deal with wrapping around at the end

				int BytesToMove = QHead - QTail;
				for( int i=0; i<BytesToMove; i++ ) {
					rxQueue[i] = rxQueue[QTail + i];
				}

				// Reset the QTail back to zero (start of the buffer) and adjust the QHead to how many bytes we have
				QTail = 0;
				QHead = BytesToMove;


				// Check to see if the user switched to a new tab, and update the flight controller if they did
				// This is done here because it's safe - it means we don't have to worry about locking the serial
				// port object or anything weird because we only use it in one place

				Mode tempMode = (Mode)(tcMainTabs.SelectedIndex+1);
				if(tempMode != currentMode) {

					uint written = 0;
					if(currentMode == Mode.GyroCalibration) {
						txBuffer[0] = 0x11;
						ftdi.Write( txBuffer, 1, ref written );	// Reset to previous drift values
					}
					else if(currentMode == Mode.AccelCalibration)
					{
						txBuffer[0] = 0x15;
						ftdi.Write( txBuffer, 1, ref written );	// Reset to previous offset values
					}

					currentMode = tempMode;
					txBuffer[0] = OutputMode[ (byte)currentMode ];
					ftdi.Write( txBuffer, 1, ref written );	// Which mode are we in?

					if(currentMode == Mode.GyroCalibration) {
						txBuffer[0] = 0x10;
						ftdi.Write( txBuffer, 1, ref written );	// Zero drift calibration values
					}
					else if(currentMode == Mode.AccelCalibration)
					{
						txBuffer[0] = 0x14;
						ftdi.Write( txBuffer, 1, ref written );	// Zero offset calibration values
					}
				}
			}

			catch( Exception )
			{
				ftdi.Close();	//  comm = null;
			}
		}

		short clamp( int v, int min, int max )
		{
			if(v < min) return (short)min;
			if(v > max) return (short)max;
			return (short)v;
		}


		private void ProcessPacket( byte packetType )
		{
			QTail += 3;	// Skip the header signature and the packet type values

			// Figure out what mode we're in and read the complete packet
			switch( packetType )
			{
				case 0x01:	// Receiver test packet - currently 4 words of data
					Thro = GetCommWord();
					Aile = GetCommWord();
					Elev = GetCommWord();
					Rudd = GetCommWord();
					Gear = GetCommWord();
					Aux1 = GetCommWord();
					Aux2 = GetCommWord();
					Aux3 = GetCommWord();

					if(currentMode == Mode.RadioTest)
					{
						rsLeft.XValue = (float)Rudd;
						rsLeft.YValue = (float)Thro;

						rsRight.XValue = (float)Aile;
						rsRight.YValue = (float)Elev;

						lblGear.Text = Gear.ToString();
						lblAux1.Text = Aux1.ToString();
						lblAux2.Text = Aux2.ToString();
						lblAux3.Text = Aux3.ToString();

						if(Gear < -512) {
							lblFlightMode.Text = "Assisted";
						}
						else if(Gear > 512) {
							lblFlightMode.Text = "Manual";
						}
						else {
							lblFlightMode.Text = "Assisted";	// Will become auto
						}
					}
					//GotPacket = true;
					break;


				case 0x02:	// Sensor test packet
					GyroTemp = GetCommWord();
					GyroX = GetCommWord();
					GyroY = GetCommWord();
					GyroZ = GetCommWord();

					AccelX = GetCommWord();
					AccelY = GetCommWord();
					AccelZ = GetCommWord();

					MagX = GetCommWord();
					MagY = GetCommWord();
					MagZ = GetCommWord();
					Battery = GetCommWord();

					prevAlt = Alt;
					Alt = GetCommLong();
					AltTemp = GetCommLong();
					AltEst = GetCommLong();


					// If we're on the sensor test tab, update those UI controls
					if(currentMode == Mode.SensorTest)
					{
						gGyroX.Value = (float)GyroX;
						gGyroY.Value = (float)GyroY;
						gGyroZ.Value = (float)GyroZ;

						gAccelX.Value = (float)AccelX;
						gAccelY.Value = (float)AccelY;
						gAccelZ.Value = (float)AccelZ;
						gGyroTemp.Value = (float)GyroTemp;

						gMagX.Value = (float)MagX;
						gMagY.Value = (float)MagY;
						gMagZ.Value = (float)MagZ;

						lblBattery.Text = string.Format( "{0:0.00} v", (float)Battery / 100.0 );

						// Compute a heading from the magnetometer readings (not tilt compensated)
						//gHeading.Value = (float)(Math.Atan2( MagX, MagY ) * 1800.0/Math.PI);
						gHeading.Value = ComputeTiltCompensatedHeading();

						float altTempDegrees = 42.5f + (float)AltTemp / 480.0f;

						lblAltimeter.Text = string.Format( "{0:0.000} m", (float)AltEst / 1000.0f );	// Altimeter output is in mm
						lblAltimeterTemp.Text = string.Format( "{0:0.00}*C", altTempDegrees );


						int[] sample = {AltEst, Alt, Alt };
						SampleCounter++;
						bool DoUpdate = (SampleCounter & 15) == 15;
						gAltimeter.AddSample( sample, true );
						if( DoUpdate ) gAltimeter.UpdateStats();
					}


					// If we're on the sensor calibration tab, update the graph / line fit controls
					if(currentMode == Mode.GyroCalibration)
					{
						LineFit.Sample lfSample = new LineFit.Sample();
						lfSample.t = GyroTemp;
						lfSample.x = GyroX;
						lfSample.y = GyroY;
						lfSample.z = GyroZ;
						SampleCounter++;

						bool DoRedraw = (tcMainTabs.SelectedIndex == 3) && ((SampleCounter & 7) == 7);

						lfGraph.AddSample( lfSample, DoRedraw );

						if( DoRedraw == false )
						{
							gCalibTemp.Value = (float)GyroTemp;
							gCalibX.Value = (float)GyroX;
							gCalibY.Value = (float)GyroY;
							gCalibZ.Value = (float)GyroZ;

							int scaleX = 0;
							int scaleY = 0;
							int scaleZ = 0;

							if( Math.Abs( lfGraph.dSlope.x) > 0.00001)
								scaleX = (int)Math.Round( 1.0 / lfGraph.dSlope.x );

							if( Math.Abs( lfGraph.dSlope.y) > 0.00001)
								scaleY = (int)Math.Round( 1.0 / lfGraph.dSlope.y );

							if( Math.Abs( lfGraph.dSlope.z) > 0.00001)
								scaleZ = (int)Math.Round(1.0 / lfGraph.dSlope.z);

							int offsetX = (int)Math.Round( lfGraph.dIntercept.x );
							int offsetY = (int)Math.Round( lfGraph.dIntercept.y );
							int offsetZ = (int)Math.Round( lfGraph.dIntercept.z );

							if(Math.Abs( scaleX ) < 1024.0f)
								gxScale.Text = scaleX.ToString();
							else
								gxScale.Text = "0";

							if(Math.Abs( scaleY ) < 1024.0f)
								gyScale.Text = scaleY.ToString();
							else
								gyScale.Text = "0";

							if(Math.Abs( scaleZ ) < 1024.0f)
								gzScale.Text = scaleZ.ToString();
							else
								gzScale.Text = "0";

							gxOffset.Text = offsetX.ToString();
							gyOffset.Text = offsetY.ToString();
							gzOffset.Text = offsetZ.ToString();
						}
					}


					if(currentMode == Mode.AccelCalibration)
					{
						gAccelXCal.Value = (float)AccelX;
						gAccelYCal.Value = (float)AccelY;
						gAccelZCal.Value = (float)AccelZ;
					}
					break;


				case 0x06:	// Vibration test packet
					GyroX = GetCommWord();
					GyroY = GetCommWord();
					GyroZ = GetCommWord();

					if(currentMode == Mode.VibrationTest)
					{
						int[] gySample = new int[3];
						gySample[0] = GyroX;
						gySample[1] = GyroY;
						gySample[2] = GyroZ;
						grGyro.AddSample( gySample, true );

						SampleCounter++;

						if((SampleCounter & 255) == 255)
						{
							grGyro.UpdateStats();
							lblGXMin.Text = grGyro.Mins[0].ToString();
							lblGXMax.Text = grGyro.Maxs[0].ToString();
							lblGXAvg.Text = grGyro.Avgs[0].ToString( "00.0000" );
							lblGXVar.Text = grGyro.Vars[0].ToString( "0.00000" );

							lblGYMin.Text = grGyro.Mins[1].ToString();
							lblGYMax.Text = grGyro.Maxs[1].ToString();
							lblGYAvg.Text = grGyro.Avgs[1].ToString( "00.0000" );
							lblGYVar.Text = grGyro.Vars[1].ToString( "0.00000" );

							lblGZMin.Text = grGyro.Mins[2].ToString();
							lblGZMax.Text = grGyro.Maxs[2].ToString();
							lblGZAvg.Text = grGyro.Avgs[2].ToString( "00.0000" );
							lblGZVar.Text = grGyro.Vars[2].ToString( "0.00000" );
						}
					}
					break;


				case 0x04:	// IMU Test - Update the orientation quaternion
					Quaternion q = new Quaternion();
					q.x = GetCommFloat();
					q.y = GetCommFloat();
					q.z = GetCommFloat();
					q.w = GetCommFloat();

					Pitch = GetCommWord();
					Roll = GetCommWord();
					Yaw = GetCommWord();

					ocOrientation.Quat = q;
					gPitch.Value = Pitch;
					gRoll.Value = Roll;
					gYaw.Value = Yaw;
					break;

				case 0x05:	// IMU Comparison - Test different orientation update methods
					GyroX = GetCommWord();
					GyroY = GetCommWord();
					GyroZ = GetCommWord();

					AccelX = GetCommWord();
					AccelY = GetCommWord();
					AccelZ = GetCommWord();

					prevAlt = Alt;
					Alt = GetCommLong();

					ComputeQuaternionOrientations();

					ocCompQ1.Quat = Q1;
					ocCompQ2.Quat = Q2;
					break;


				case 7:	// Everything mode
					{
						int phase = GetCommByte();
						switch(phase)
						{
							case 0:
								Everything.Thro = GetCommShort();
								Everything.Aile = GetCommShort();
								Everything.Elev = GetCommShort();
								Everything.Rudd = GetCommShort();
								Everything.Gear = GetCommShort();				// First 20 bytes
								Everything.Aux1 = GetCommShort();
								Everything.Aux2 = GetCommShort();
								Everything.Aux3 = GetCommShort();
								Everything.BatteryVolts = GetCommShort();
								Everything.padding = GetCommShort();

								rjE_Left.XValue = Everything.Rudd;
								rjE_Left.YValue = Everything.Thro;

								rjE_Right.XValue = Everything.Aile;
								rjE_Right.YValue = Everything.Elev;

								tbGear.Value = clamp( Everything.Gear + 1024, 0, 2048);
								tbAux1.Value = clamp( Everything.Aux1 + 1024, 0, 2048);
								tbAux2.Value = clamp( Everything.Aux2 + 1024, 0, 2048);
								tbAux3.Value = clamp( Everything.Aux3 + 1024, 0, 2048);
								break;

							case 1:
								Everything.Temp = GetCommShort();
								Everything.GyroX = GetCommShort();
								Everything.GyroY = GetCommShort();
								Everything.GyroZ = GetCommShort();
								Everything.AccelX = GetCommShort();				// 2nd 20 bytes
								Everything.AccelY = GetCommShort();
								Everything.AccelZ = GetCommShort();
								Everything.MagX = GetCommShort();
								Everything.MagY = GetCommShort();
								Everything.MagZ = GetCommShort();

								int[] gySample = { Everything.GyroX, Everything.GyroY, Everything.GyroZ };
								grE_Gyro.AddSample( gySample, true );

								int[] accSample = { Everything.AccelX, Everything.AccelY, Everything.AccelZ };
								grE_Accel.AddSample( accSample, true );

								SampleCounter++;
								bool DoUpdate = (SampleCounter & 15) == 15;
								grE_Gyro.UpdateStats();
								grE_Accel.UpdateStats();
								break;

							case 2:
								Everything.Alt = GetCommLong();
								Everything.AltRate = GetCommLong();
								Everything.Pressure = GetCommLong();			// 3rd 20 bytes
								Everything.Pitch = GetCommLong();
								Everything.Roll = GetCommLong();
								break;

							case 3:
								Everything.Yaw = GetCommLong();
								Everything.q.x = GetCommFloat();
								Everything.q.y = GetCommFloat();				// Last 20 bytes
								Everything.q.z = GetCommFloat();
								Everything.q.w = GetCommFloat();

								ocCube.Quat = Everything.q;
								break;

						}
					}
					break;
			}
		}

		private float ComputeTiltCompensatedHeading()
		{
			// Compute pitch and roll from the current accelerometer vector - only accurate if stationary
			Vector v = new Vector( AccelX, AccelY, AccelZ );
			v = v.Normalize();

			float accPitch = (float)Math.Asin( -v.x );
			float accRoll =  (float)Math.Asin( v.y / Math.Cos(accPitch) );

			// Technically we should also calibrate the min/max readings from the mag first - this may not be accurate otherwise

			float Mxh = (float)(MagX * Math.Cos( accPitch ) + MagZ * Math.Sin( accPitch ));
			float Myh = (float)(MagX * Math.Sin( accRoll ) * Math.Sin( accPitch ) + MagY * Math.Cos( accRoll ) - MagZ * Math.Sin( accRoll ) * Math.Cos( accPitch ));

			float Heading = (float)(Math.Atan2( Mxh, Myh ) * 1800.0 / Math.PI);
			return Heading;
		}


		private void tcMainTabs_SelectedIndexChanged( object sender, EventArgs e )
		{
			// I used to handle the mode switch here, but it was causing problems
			// so I moved it into the timer-tick handler

			if( ftdi.IsOpen ) {
				//currentMode = (Mode)(tcMainTabs.SelectedIndex+1);
				//txBuffer[0] = (byte)currentMode;
				//comm.Write( txBuffer, 0, 1 );	// Which mode are we in?
			}
		}

		private void TestMotor( int MotorIndex )
		{
			if( ftdi.IsOpen ) {
				txBuffer[0] = (byte)(MotorIndex | 8);
				uint written = 0;
				ftdi.Write( txBuffer, 1, ref written );
			}
		}


		private void btnMotor1_Click( object sender, EventArgs e ) {
			TestMotor( 0 );
		}

		private void btnMotor2_Click( object sender, EventArgs e ) {
			TestMotor( 1 );
		}

		private void btnMotor3_Click( object sender, EventArgs e ) {
			TestMotor( 2 );
		}

		private void btnMotor4_Click( object sender, EventArgs e ) {
			TestMotor( 3 );
		}

		private void btnBeeper_Click( object sender, EventArgs e ) {
			TestMotor( 4 );
		}

		private void btnLED_Click( object sender, EventArgs e ) {
			TestMotor( 5 );
		}

		private void btnResetCalib_Click( object sender, EventArgs e )
		{
			lfGraph.Reset();
		}


		private void btnUploadCalibration_Click( object sender, EventArgs e )
		{
			// Upload calibration data
			txBuffer[0] = 0x12;

			int scaleX = (int)Math.Round( 1.0 / lfGraph.dSlope.x );
			int scaleY = (int)Math.Round( 1.0 / lfGraph.dSlope.y );
			int scaleZ = (int)Math.Round( 1.0 / lfGraph.dSlope.z );
			int offsetX = (int)Math.Round( lfGraph.dIntercept.x);
			int offsetY = (int)Math.Round( lfGraph.dIntercept.y);
			int offsetZ = (int)Math.Round( lfGraph.dIntercept.z);

			txBuffer[1] = (byte)(scaleX >> 8);
			txBuffer[2] = (byte)(scaleX >> 0);
			txBuffer[3] = (byte)(scaleY >> 8);
			txBuffer[4] = (byte)(scaleY >> 0);
			txBuffer[5] = (byte)(scaleZ >> 8);
			txBuffer[6] = (byte)(scaleZ >> 0);
			txBuffer[7] = (byte)(offsetX >> 8);
			txBuffer[8] = (byte)(offsetX >> 0);
			txBuffer[9] = (byte)(offsetY >> 8);
			txBuffer[10] = (byte)(offsetY >> 0);
			txBuffer[11] = (byte)(offsetZ >> 8);
			txBuffer[12] = (byte)(offsetZ >> 0);

			uint written = 0;
			ftdi.Write( txBuffer, 13, ref written );
			// TODO: make sure all bytes were written
		}


		private void btnThrottleCalibrate_Click( object sender, EventArgs e )
		{
			uint written = 0;
			switch(CalibrationCycle)
			{
				case 0:
					TestMotor( 6 );
					lblCalibrateDocs.Text = "Throttle calibration has started.  Be sure your flight battery is UNPLUGGED, then press the Throttle Calibration button again";
					CalibrationCycle = 1;

					// TODO - Should probably disable all other buttons, and make an abort button visible
					break;

				case 1:
					txBuffer[0] = (byte)0xFF;
					ftdi.Write( txBuffer, 1, ref written );
					lblCalibrateDocs.Text = "Plug in your flight battery and wait for the ESCs to beep twice, then press the Throttle Calibration button again";
					CalibrationCycle = 2;
					break;

				case 2:
					txBuffer[0] = (byte)Mode.MotorTest;
					ftdi.Write( txBuffer, 1, ref written );
					lblCalibrateDocs.Text = "Calibration complete";
					lblCalibrateDocs.Update();
					System.Threading.Thread.Sleep( 1000 * 3 );
					lblCalibrateDocs.Text = "";
					CalibrationCycle = 0;

					// TODO: Re-enable all other buttons, hide the abort button

					break;
			}
		}

		private void MainForm_FormClosing( object sender, FormClosingEventArgs e )
		{
			tickTimer.Enabled = false;

			try
			{
				currentMode = Mode.None;
				txBuffer[0] = (byte)(currentMode);

				if(null != ftdi && ftdi.IsOpen == true)
				{
					uint written = 0;
					ftdi.Write( txBuffer, 1, ref written );
				}
			}

			catch(Exception)
			{
			}
		}


		private void MainForm_FormClosed( object sender, FormClosedEventArgs e )
		{
			if( ftdi != null && ftdi.IsOpen) {
				ftdi.Close();
			}
		}


		private void btnMotor1_MouseDown( object sender, MouseEventArgs e )
		{
			((Button)sender).Capture = true;
			TestMotor( 0 );
		}

		private void btnMotor2_MouseDown( object sender, MouseEventArgs e )
		{
			((Button)sender).Capture = true;
			TestMotor( 1 );
		}

		private void btnMotor3_MouseDown( object sender, MouseEventArgs e )
		{
			((Button)sender).Capture = true;
			TestMotor( 2 );
		}

		private void btnMotor4_MouseDown( object sender, MouseEventArgs e )
		{
			((Button)sender).Capture = true;
			TestMotor( 3 );
		}

		private void btnMotor_MouseUp( object sender, MouseEventArgs e )
		{
			((Button)sender).Capture = false;
			TestMotor( 7 );	// Turn off all motors
		}


		float errCorrX1 = 0;
		float errCorrY1 = 0;
		float errCorrZ1 = 0;

		float errCorrX2 = 0;
		float errCorrY2 = 0;
		float errCorrZ2 = 0;

		int GZeroX = 0, GZeroY = 0, GZeroZ = 0;
		int GZeroCount = 0;

		const float GyroToDeg = 1000.0f / (17.5f * 4);
		const float RadToDeg = 180.0f / 3.141592654f;
		const float UpdateRate = 250.0f / 2.0f;	// Update rate is 250 hz, but every other cycle
		const float GyroInvScale = GyroToDeg * RadToDeg * UpdateRate;
		const float GyroScale = 1.0f / GyroInvScale;

		const float AccScale = 1.0f / OneG;

		Quaternion Q1 = new Quaternion( 1, 0, 0, 0 );
		Quaternion Q2 = new Quaternion( 1, 0, 0, 0 );
		Quaternion qdot = new Quaternion();

		Matrix m = new Matrix();
		Matrix m2 = new Matrix();
		Vector acc = new Vector();


		// Test different methods of updating orientation to see what works best
		void ComputeQuaternionOrientations()
		{
			if(GZeroCount < 64)
			{
				GZeroX += GyroX;
				GZeroY += GyroY;
				GZeroZ += GyroZ;

				GZeroCount++;
				if(GZeroCount == 64)
				{
					GZeroX /= 64;
					GZeroY /= 64;
					GZeroZ /= 64;
				}
				return;
			}

			ComputeQuatOriginalMethod();	// Original, as implemented on the Prop
			ComputeQuatAlternateMethod();	// Testing alternate

			ComputeAltitudeEstimate();
		}


		void ComputeQuatOriginalMethod()
		{
			float rx = (float)(GyroX - GZeroX) *  GyroScale + errCorrX1;
			float ry = (float)(GyroZ - GZeroZ) * -GyroScale + errCorrY1;
			float rz = (float)(GyroY - GZeroY) * -GyroScale + errCorrZ1;

			float rMag = (float)(Math.Sqrt(rx * rx + ry * ry + rz * rz + 0.0000000001) * 0.5);

			float cosr = (float)Math.Cos(rMag);
			float sinr = (float)Math.Sin(rMag) / rMag;

			qdot.w = -(rx * Q1.x + ry * Q1.y + rz * Q1.z) * 0.5f;
			qdot.x =  (rx * Q1.w + rz * Q1.y - ry * Q1.z) * 0.5f;
			qdot.y =  (ry * Q1.w - rz * Q1.x + rx * Q1.z) * 0.5f;
			qdot.z =  (rz * Q1.w + ry * Q1.x - rx * Q1.y) * 0.5f;

			Q1.w = cosr * Q1.w + sinr * qdot.w;
			Q1.x = cosr * Q1.x + sinr * qdot.x;
			Q1.y = cosr * Q1.y + sinr * qdot.y;
			Q1.z = cosr * Q1.z + sinr * qdot.z;

			Q1 = Q1.Normalize();

			// Convert to matrix form
			m.From(Q1);

			// Compute the difference between the accelerometer vector and the matrix Y (up) vector
			acc = new Vector( -AccelX, AccelZ, AccelY );
			rMag = acc.Length * AccScale;

			acc = acc.Normalize();
			float accWeight = 1.0f - Math.Min( Math.Abs( 2.0f - rMag * 2.0f ), 1.0f );

			float errDiffX = acc.y * m.m[1,2] - acc.z * m.m[1,1];
			float errDiffY = acc.z * m.m[1,0] - acc.x * m.m[1,2];
			float errDiffZ = acc.x * m.m[1,1] - acc.y * m.m[1,0];

			accWeight *= 1.0f / 512.0f;
			errCorrX1 = errDiffX * accWeight;
			errCorrY1 = errDiffY * accWeight;
			errCorrZ1 = errDiffZ * accWeight;
		}


		float lastGx = 0;
		float lastGy = 0;
		float lastGz = 0;

		void ComputeQuatAlternateMethod()
		{
			// Trapezoidal integration of gyro readings
			float rx = (float)((GyroX+lastGx)*0.5f - GZeroX) *  GyroScale + errCorrX2;
			float ry = (float)((GyroZ+lastGz)*0.5f - GZeroZ) * -GyroScale + errCorrY2;
			float rz = (float)((GyroY+lastGy)*0.5f - GZeroY) * -GyroScale + errCorrZ2;

			lastGx = GyroX;
			lastGy = GyroY;
			lastGz = GyroZ;

			float rMag = (float)(Math.Sqrt(rx * rx + ry * ry + rz * rz + 0.0000000001) * 0.5);

			float cosr = (float)Math.Cos(rMag);
			float sinr = (float)Math.Sin(rMag) / rMag;

			qdot.w = -(rx * Q2.x + ry * Q2.y + rz * Q2.z) * 0.5f;
			qdot.x =  (rx * Q2.w + rz * Q2.y - ry * Q2.z) * 0.5f;
			qdot.y =  (ry * Q2.w - rz * Q2.x + rx * Q2.z) * 0.5f;
			qdot.z =  (rz * Q2.w + ry * Q2.x - rx * Q2.y) * 0.5f;

			Q2.w = cosr * Q2.w + sinr * qdot.w;
			Q2.x = cosr * Q2.x + sinr * qdot.x;
			Q2.y = cosr * Q2.y + sinr * qdot.y;
			Q2.z = cosr * Q2.z + sinr * qdot.z;

			Q2 = Q2.Normalize();

			// Convert to matrix form
			m2.From(Q2);

			// Compute the difference between the accelerometer vector and the matrix Y (up) vector
			acc = new Vector( -AccelX, AccelZ, AccelY );
			rMag = acc.Length * AccScale;

			acc = acc.Normalize();
			float accWeight = 1.0f - Math.Min( Math.Abs( 2.0f - rMag * 2.0f ), 1.0f );
			// accWeight *= accWeight * 4.0f;

			float errDiffX = acc.y * m2.m[1,2] - acc.z * m2.m[1,1];
			float errDiffY = acc.z * m2.m[1,0] - acc.x * m2.m[1,2];
			float errDiffZ = acc.x * m2.m[1,1] - acc.y * m2.m[1,0];

			accWeight *= 1.0f / 512.0f;
			errCorrX2 = errDiffX * accWeight;
			errCorrY2 = errDiffY * accWeight;
			errCorrZ2 = errDiffZ * accWeight;

			// At this point, errCorr represents a very small correction rotation vector, but in the WORLD frame
			// Rotate it into the current BODY frame

			//Vector errVect = new Vector( errCorrX2, errCorrY2, errCorrZ2 );
			//errVect = m.Transpose().Mul( errVect );
			//errCorrX2 = errVect.x;
			//errCorrY2 = errVect.y;
			//errCorrZ2 = errVect.z;
		}


		private void ComputeAltitudeEstimate()
		{
			acc = new Vector( -AccelX, AccelZ, AccelY );
			acc *= 1.0f * AccScale;

			// Get gravity vector from orentation matrix
			Vector gravity = new Vector( m.m[1,0], m.m[1,1], m.m[1,2] );

			// Subtract from accelerometer vector to get directional forces
			acc -= gravity;

			// acc is now m/s^2
			acc *= 9.8f;

			// Orient accelerometer vector (or at least just Z component)
			Vector forceW = m.Transpose().Mul( acc );

			// Compute the vertical velocity from the altimeter
			altVelocity = ((float)(Alt - prevAlt) / 1000.0f) * UpdateRate;	// Now in M/s

			// Integrate accelerometer vector to get velocity (m/sec)
			velocityEstimate += forceW * (1.0f / UpdateRate);

			// Use the altimeter velocity estimate to drift correct the computed velocity
			velocityEstimate.y = velocityEstimate.y * 0.998f + altVelocity * 0.002f;


			// Integrate Z velocity to get approximate height (in meters)
			positionEstimate += velocityEstimate * (1.0f / UpdateRate);

			// Slowly un-drift the Y position estimate with the altimeter over time
			positionEstimate.y = (positionEstimate.y * 0.998f) + (((float)Alt / 1000.0f) * 0.002f);

			lblStatOutput.Text = string.Format( "{0:0.00}   {1:0.000}", positionEstimate.y, velocityEstimate.y );
		}



		void GenerateAltitudeTable()
		{
			// Generate a table of pressure to altitude values for the range of pressures supported by our pressure sensor

			//float altitudeFeet = (float)((1.0 - Math.Pow( pressure / 1013.25, 0.190284 )) * 145442.16);
			//float altitudeMeters = altitudeFeet / 3.280839895f;

			// Range is 260 to 1260 hPa
			for(int HPA = 260; HPA <= 1260; HPA += 4)
			{
				int Index = (HPA - 260) / 4;

				double hPa = HPA;
				double alt = (Math.Pow( 10.0, Math.Log10( hPa / 1013.25 ) / 5.2558797) - 1.0) / (-6.8755856 * 0.000001);
				double alt_mm = (alt / 3.280839895) * 1000.0;

				AltTable[Index] = (int)(alt_mm + 0.5);

				//un-comment the next line to output a DAT formatted table to the console output window
				//Console.WriteLine( "	AltTable_{0:000}	long	{1}", Index, AltTable[Index] );
			}

			/*
			// This code tests the average and maxumum error over the entire range of the altitude table

			double maxErr = 0.0;
			double avgErr = 0.0;
			int Count = 0;

			double minAlt = float.MaxValue;
			double maxAlt = float.MinValue;

			// Test over the range of values we get from the altimeter (every 8th is enough)
			for(int i = 260 * 4096; i < 1260 * 4096; i+=8 )
			//for(int i = 870 * 4096; i < 1040 * 4096; i += 8)
			{
				double tabAlt = GetTableAltitude( i );
				double compAlt = GetComputedAltitude( i );

				minAlt = Math.Min( minAlt, compAlt );
				maxAlt = Math.Max( maxAlt, compAlt );

				double diff = Math.Abs(tabAlt - compAlt);

				maxErr = Math.Max( maxErr, diff );
				avgErr += diff;
				Count++;
			}

			avgErr /= (double)Count;
			Console.WriteLine( "Table Error : Max: {0:0.0000} ft, Avg: {1:0.0000} ft", maxErr, avgErr );
			Console.WriteLine( "From alt: {0:0.00} ft to {1:0.00} ft", minAlt , maxAlt );

			double alt1 = GetTableAltitude( 700 * 4096 );
			double alt2 = GetTableAltitude( 700 * 4096 + 1 );

			Console.WriteLine( "Resolved diff of {0:0.0000} ft", Math.Abs(alt2 - alt1) );
			//*/
		}


		// Use the lookup table to compute altitude from an altimeter pressure reading (hPa*4096)
		float GetTableAltitude( int Alt )
		{
			int Index = ((Alt >> 12) - 260) / 4;
			if(Index < 0 || Index > 250) return 0.0f;

			int A1 = ((Index * 4) + 260) << 12;
			int A2 = (((Index+1) * 4) + 260) << 12;
			int Delta = A2 - A1;	// Always 16384
			int Frac = Alt - A1;

			int Tab1 = AltTable[Index];
			int Tab2 = AltTable[Index+1];

			int ResultMM = ((Tab2 - Tab1) * Frac + Delta/2) / Delta + Tab1;
			float ResultFeet = (float)ResultMM / (25.4f * 12.0f);

			return ResultFeet;
		}


		// Use double-precision math to compute altitude from an altimeter pressure reading (hPa*4096)
		float GetComputedAltitude( int Alt )
		{
			double pressure = (double)Alt / 4096.0;
			float altFeet = (float)((Math.Pow( 10.0, Math.Log10( pressure / 1013.25 ) / 5.2558797 ) - 1.0) / (-6.8755856 * 0.000001));
			return altFeet;
		}


		private void GetAccelAvgSample( int i )
		{
			Label[] labels = { lblAccelCal1, lblAccelCal2, lblAccelCal3, lblAccelCal4 };

			accXCal[i] = gAccelXCal.MovingAverage;
			accYCal[i] = gAccelYCal.MovingAverage;
			accZCal[i] = gAccelZCal.MovingAverage;

			labels[i].Text = string.Format( "{0}, {1}, {2}", accXCal[i].ToString( "F1" ), accYCal[i].ToString( "F1" ), accZCal[i].ToString( "F1" ) );
		}


		private void btnAccelCal1_Click( object sender, EventArgs e )
		{
			GetAccelAvgSample( 0 );
		}

		private void btnAccelCal2_Click( object sender, EventArgs e )
		{
			GetAccelAvgSample( 1 );
		}

		private void btnAccelCal3_Click( object sender, EventArgs e )
		{
			GetAccelAvgSample( 2 );
		}

		private void btnAccelCal4_Click( object sender, EventArgs e )
		{
			GetAccelAvgSample( 3 );
		}

		private void btnUploadAccelCal_Click( object sender, EventArgs e )
		{
			float fx = 0.0f, fy = 0.0f, fz = 0.0f;
			for(int i = 0; i < 4; i++)
			{
				fx += accXCal[i] * 0.25f;
				fy += accYCal[i] * 0.25f;
				fz += accZCal[i] * 0.25f;
			}

			int ax = (int)Math.Round( fx );
			int ay = (int)Math.Round( fy );
			int az = (int)Math.Round( fz );

			lblAccelCalFinal.Text = string.Format( "{0}, {1}, {2}", ax, ay, az );

			az -= OneG;

			// Upload calibration data
			txBuffer[0] = 0x16;

			txBuffer[1] = (byte)(ax >> 8);
			txBuffer[2] = (byte)(ax >> 0);
			txBuffer[3] = (byte)(ay >> 8);
			txBuffer[4] = (byte)(ay >> 0);
			txBuffer[5] = (byte)(az >> 8);
			txBuffer[6] = (byte)(az >> 0);

			uint written = 0;
			ftdi.Write( txBuffer, 7, ref written );
			// TODO: make sure all bytes were written
		}


		private void btnUploadAngleCorrection_Click( object sender, EventArgs e )
		{
			// Upload calibration data
			txBuffer[0] = 0x17;

			double rollOffset = (float)((double)udRollCorrection.Value * Math.PI / 180.0);
			double pitchOffset = (float)((double)udPitchCorrection.Value * Math.PI / 180.0);

			byte[] rollSinBytes = BitConverter.GetBytes( (float)Math.Sin(rollOffset) );
			byte[] rollCosBytes = BitConverter.GetBytes( (float)Math.Cos(rollOffset) );
			byte[] pitchSinBytes = BitConverter.GetBytes( (float)Math.Sin(pitchOffset) );
			byte[] pitchCosBytes = BitConverter.GetBytes( (float)Math.Cos(pitchOffset) );

			rollSinBytes.CopyTo( txBuffer, 1 );
			rollCosBytes.CopyTo( txBuffer, 5 );
			pitchSinBytes.CopyTo( txBuffer, 9 );
			pitchCosBytes.CopyTo( txBuffer, 13 );

			uint written = 0;
			ftdi.Write( txBuffer, 17, ref written );
			// TODO: make sure all bytes were written
		}


		private void btnRecPWM_CheckedChanged( object sender, EventArgs e )
		{
			if(btnRecPWM.Checked)
			{
				txBuffer[0] = 0x18;	// Set receiver type
				txBuffer[1] = 0;	// PWM

				uint written = 0;
				ftdi.Write( txBuffer, 2, ref written );
			}
		}

		private void btnRecSBUS_CheckedChanged( object sender, EventArgs e )
		{
			if(btnRecSBUS.Checked)
			{
				txBuffer[0] = 0x18;	// Set receiver type
				txBuffer[1] = 1;	// SBUS

				uint written = 0;
				ftdi.Write( txBuffer, 2, ref written );
			}
		}
	}
}
