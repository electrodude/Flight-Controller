﻿/*
  Elev8 GroundStation - V1.0

  Copyright 2015 Parallax Inc

  This work is licensed under a Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.
  http://creativecommons.org/licenses/by-nc-sa/4.0/

  Written by Jason Dorie
*/


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Windows.Forms;

using GraphLib;


namespace Elev8
{
	public partial class MainForm : Form
	{
#if GENERIC
		Connection comm = new Connection_Serial();
#else
		Connection comm = new Connection_FTDI();
#endif
		CommStatus status = CommStatus.Initializing;

		const int OneG = 4096;

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


		enum RADIO_MODE
		{
			Mode1,		// (Rudder, Elevator)  (Aileron,Throttle)
			Mode2		// (Rudder, Throttle)  (Aileron,Elevator)
		};

		RADIO_MODE RadioMode = RADIO_MODE.Mode2;	// North American default


		RadioData radio = new RadioData();
		SensorData sensors = new SensorData();
		Quaternion q = new Quaternion();
		Quaternion cq = new Quaternion();	// Control (target) quaternion
		MotorData motors = new MotorData();
		ComputedData computed = new ComputedData();
		DebugValues debugData = new DebugValues();

		PREFS prefs = new PREFS();

		int CalibrationCycle = 0;		// For throttle (ESC) output calibration

		bool InternalChange = false;
		byte[] txBuffer = new byte[10];
		int Heartbeat = 0;

		//bool PrefsReceived = false;


		ComboBox[] channelAssignControls = new ComboBox[8];

		string[] GraphNames = new string[] { "GX", "GY", "GZ", "AX", "AY", "AZ", "MX", "MY", "MZ", "AltBaro", "AltEsti", "Pitch", "Roll", "Yaw", "Voltage" };
		Color[] GraphColors = new Color[] {Color.Red, Color.Green, Color.Blue, Color.DarkRed, Color.DarkGreen, Color.DarkBlue,
			Color.LightGreen, Color.LightSalmon, Color.LightBlue, Color.DarkGray, Color.Gray, Color.DarkGoldenrod, Color.Cyan, Color.Orange, Color.Purple };

		DataSource[] graphSources = new DataSource[15];
		const int NumGraphDisplaySamples = 512;
		int SampleIndex = 0;



		public string AssemblyVersion
		{
			get {
				return Assembly.GetExecutingAssembly().GetName().Version.ToString();
			}
		}


		public MainForm()
		{
			InternalChange = true;
			InitializeComponent();

			string modeString = Properties.Settings.Default.RadioMode;
			if(modeString != null)
			{
				if(modeString == "Mode1") RadioMode = RADIO_MODE.Mode1;
				if(modeString == "Mode2") RadioMode = RADIO_MODE.Mode2;
			}

			tssGSVersion.Text = string.Format( "GroundStation version {0}", AssemblyVersion );

			channelAssignControls = new ComboBox[] {
				cbChannel1, cbChannel2, cbChannel3, cbChannel4,
				cbChannel5, cbChannel6, cbChannel7, cbChannel8 };

			for(int i = 0; i < 8; i++)
			{
				for(int j = 0; j < 8; j++) {
					if(i == j) {
						channelAssignControls[i].Items.Add( "Ch " + (j + 1).ToString() + '*');
					}
					else {
						channelAssignControls[i].Items.Add( "Ch " + (j + 1).ToString() );
					}
				}

				channelAssignControls[i].SelectedIndex = i;
			}

			comm.ConnectionStarted += new ConnectionEvent( comm_ConnectionStarted );

			SetRadioMode( RadioMode );
			InternalChange = false;

			plotSensors.DataSources.Clear();
			plotSensors.SetDisplayRangeX( 0, NumGraphDisplaySamples );


			for(int i = 0; i < 15; i++)
			{
				graphSources[i] = new DataSource();
				graphSources[i].Name = GraphNames[i];
				graphSources[i].Active = false;

				plotSensors.DataSources.Add( graphSources[i] );

				graphSources[i].Length = NumGraphDisplaySamples;
                plotSensors.PanelLayout = PlotterGraphPaneEx.LayoutMode.NORMAL;
				graphSources[i].AutoScaleY = true;

				if(i < 6)		// GX,GY,GZ, AX,AY,AZ
				{
					graphSources[i].SetDisplayRangeY( -16384, 16384 );
					graphSources[i].SetGridDistanceY( 4096 );
					graphSources[i].AutoScaleY = true;
				}
				else if(i < 9)	// MX,MY,MZ
				{
					graphSources[i].SetDisplayRangeY( -4096, 32000 );
					graphSources[i].SetGridDistanceY( 1024 );
					graphSources[i].AutoScaleY = true;
				}
				else if(i == 9 || i == 10 )	// Altitude, m
				{
					graphSources[i].SetDisplayRangeY( -3000, 8000 );
					graphSources[i].SetGridDistanceY( 5 );
					graphSources[i].AutoScaleY = true;
				}
				else if(i < 14)	// Pitch, Roll, Yaw
				{
					graphSources[i].SetDisplayRangeY( -180, 180 );
					graphSources[i].SetGridDistanceY( 30 );
				}
				else if(i == 14)	// Voltage
				{
					graphSources[i].SetDisplayRangeY( 0, 24 );
					graphSources[i].SetGridDistanceY( 2 );
					graphSources[i].AutoScaleY = true;
				}
				else
				{
					graphSources[i].SetDisplayRangeY( -65536, 65536 );
					graphSources[i].SetGridDistanceY( 16384 );
					graphSources[i].AutoScaleY = true;
				}
				graphSources[i].GraphColor = GraphColors[i];

				ClearSamples( graphSources[i] );
				//graphSources[i].OnRenderYAxisLabel = RenderYLabel;
				//graphSources[i].OnRenderXAxisLabel += RenderXLabel;
			}

			comm.Start();
		}


		void ClearSamples( DataSource source )
		{
			for(int i = 0; i < source.Length; i++)
			{
				source.Samples[i].x = i;
				source.Samples[i].y = 0.0f;
			}
		}

		void SendCommand( string command )
		{
			txBuffer[0] = (byte)command[0];
			txBuffer[1] = (byte)command[1];
			txBuffer[2] = (byte)command[2];
			txBuffer[3] = (byte)command[3];
			comm.Send( txBuffer, 4 );
		}


		void comm_ConnectionStarted()
		{
			SendCommand( "QPRF" );	// Query the Elev8 for settings data
		}



		private void MainForm_FormClosing( object sender, FormClosingEventArgs e )
		{
			comm.Stop();
		}


		private void tmCommTimer_Tick( object sender, EventArgs e )
		{
			UpdateStatus();

			if( comm.Connected && CalibrationCycle == 0 ) {	// Don't send the heartbeat during throttle calibration
				Heartbeat++;
				if(Heartbeat == 10) {
					Heartbeat = 0;
					SendCommand( "BEAT" );	// Send the connection heartbeat
				}
			}

			ProcessPackets();

			CheckCalibrateControls();
		}


		string GearValueToFlightMode( int Value )
		{
			if(Value > 512) {
				return "Stable";		//  return "Assisted";  (Altitude hold not working properly yet - I suspect a bug in the altitude estimate)
			}
			else if(Value < -512) {
				return "Manual";
			}
			else {
				return "Stable";
			}
		}


		void ProcessPackets()
		{
			bool bRadioChanged = false;
			bool bDebugChanged = false;
			bool bSensorsChanged = false;
			bool bQuatChanged = false;
			bool bTargetQuatChanged = false;
			bool bMotorsChanged = false;
			bool bComputedChanged = false;
			bool bPrefsChanged = false;

			Packet p;
			do {
				p = comm.GetPacket();
				if(p != null)
				{
					switch( p.mode )
					{
						case 1:	// Radio data
							radio.ReadFrom( p );

							graphSources[14].Samples[SampleIndex].y = (float)radio.BatteryVolts / 100.0f;
							bRadioChanged = true;
							break;

						case 2:	// Sensor values
							sensors.ReadFrom( p );

							graphSources[0].Samples[SampleIndex].y = sensors.GyroX;
							graphSources[1].Samples[SampleIndex].y = sensors.GyroY;
							graphSources[2].Samples[SampleIndex].y = sensors.GyroZ;
							graphSources[3].Samples[SampleIndex].y = sensors.AccelX;
							graphSources[4].Samples[SampleIndex].y = sensors.AccelY;
							graphSources[5].Samples[SampleIndex].y = sensors.AccelZ;
							graphSources[6].Samples[SampleIndex].y = sensors.MagX;
							graphSources[7].Samples[SampleIndex].y = sensors.MagY;
							graphSources[8].Samples[SampleIndex].y = sensors.MagZ;

							bSensorsChanged = true;
							break;

						case 3:	// Quaternion
							{
							q.x = p.GetFloat();
							q.y = p.GetFloat();
							q.z = p.GetFloat();
							q.w = p.GetFloat();

							Matrix m = new Matrix();
							m.From( q );

							double roll = Math.Asin( m.m[1, 0] ) * (180.0 / Math.PI);
							double pitch = Math.Asin( m.m[1, 2] ) * (180.0 / Math.PI);
							double yaw = -Math.Atan2( m.m[2, 0], m.m[2, 2] ) * (180.0 / Math.PI);

							graphSources[11].Samples[SampleIndex].y = (float)pitch;
							graphSources[12].Samples[SampleIndex].y = (float)roll;
							graphSources[13].Samples[SampleIndex].y = (float)yaw;

							bQuatChanged = true;
							}
							break;

						case 4:	// Compute values
							computed.ReadFrom( p );
							bComputedChanged = true;

							graphSources[9].Samples[SampleIndex].y = (float)computed.Alt / 1000.0f;
							graphSources[10].Samples[SampleIndex].y = (float)computed.AltiEst / 1000.0f;

							SampleIndex = (SampleIndex + 1) % NumGraphDisplaySamples;
							break;

						case 5:	// Motor values
							motors.ReadFrom( p );
							bMotorsChanged = true;
							break;

						case 6:	// Control quaternion
							cq.x = p.GetFloat();
							cq.y = p.GetFloat();
							cq.z = p.GetFloat();
							cq.w = p.GetFloat();
							bTargetQuatChanged = true;
							break;

						case 7:	// Debug data
							debugData.ReadFrom( p );
							bDebugChanged = true;
							break;

						case 0x18:	// Settings
							prefs.FromBytes( p.data );
							if( prefs.CalculateChecksum() == prefs.Checksum) {
								//PrefsReceived = true;	// Global indicator of valid prefs
								bPrefsChanged = true;	// local indicator, just to set up the UI
							}
							else
							{
								SendCommand( "QPRF" );	// reqeust them again because the checksum failed
							}
							break;

					}
				}
			} while(p != null);


			if(bSensorsChanged )
			{
				if(tcTabs.SelectedTab == tpSensors)
				{
					plotSensors.Refresh();
				}

				else if( tcTabs.SelectedTab == tpGyroCalibration)
				{
					LineFit.Sample lfSample = new LineFit.Sample();
					lfSample.t = sensors.Temp;
					lfSample.x = sensors.GyroX;
					lfSample.y = sensors.GyroY;
					lfSample.z = sensors.GyroZ;

					bool DoRedraw = (SampleIndex & 3) == 3;

					lfGraph.AddSample( lfSample, DoRedraw );

					if(DoRedraw == false)
					{
						gCalibTemp.Value = (float)sensors.Temp;
						gCalibX.Value = (float)sensors.GyroX;
						gCalibY.Value = (float)sensors.GyroY;
						gCalibZ.Value = (float)sensors.GyroZ;

						int scaleX = 0;
						int scaleY = 0;
						int scaleZ = 0;

						if(Math.Abs( lfGraph.dSlope.x ) > 0.00001)
							scaleX = (int)Math.Round( 1.0 / lfGraph.dSlope.x );

						if(Math.Abs( lfGraph.dSlope.y ) > 0.00001)
							scaleY = (int)Math.Round( 1.0 / lfGraph.dSlope.y );

						if(Math.Abs( lfGraph.dSlope.z ) > 0.00001)
							scaleZ = (int)Math.Round( 1.0 / lfGraph.dSlope.z );

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

				else if( tcTabs.SelectedTab == tpAccelCalibration )
				{
					gAccelXCal.Value = sensors.AccelX;
					gAccelYCal.Value = sensors.AccelY;
					gAccelZCal.Value = sensors.AccelZ;
				}
			}


			if(bRadioChanged)
			{
				if(tcTabs.SelectedTab == tpStatus)
				{
					vbVoltage.Value = radio.BatteryVolts;
					vbVoltage.RightLabel = ((float)radio.BatteryVolts / 100.0f).ToString( "0.00" );

					if(RadioMode == RADIO_MODE.Mode2)	// North American
					{
						rsLeft.SetParameters( radio.Rudd, radio.Thro );
						rsRight.SetParameters( radio.Aile, radio.Elev );

						vbLS_YValue.RightLabel = radio.Thro.ToString();
						vbLS_YValue.Value = radio.Thro;

						vbRS_YValue.RightLabel = radio.Elev.ToString();
						vbRS_YValue.Value = radio.Elev;

					}
					else if(RadioMode == RADIO_MODE.Mode1)	// European
					{
						rsLeft.SetParameters( radio.Rudd, radio.Elev );
						rsRight.SetParameters( radio.Aile, radio.Thro );

						vbLS_YValue.RightLabel = radio.Elev.ToString();
						vbLS_YValue.Value = radio.Elev;

						vbRS_YValue.RightLabel = radio.Thro.ToString();
						vbRS_YValue.Value = radio.Thro;
					}

					vbLS_XValue.RightLabel = radio.Rudd.ToString();
					vbLS_XValue.Value = radio.Rudd;

					vbRS_XValue.RightLabel = radio.Aile.ToString();
					vbRS_XValue.Value = radio.Aile;


					vbChannel5.RightLabel = radio.Gear.ToString();
					vbChannel5.Value = radio.Gear;

					vbChannel6.RightLabel = radio.Aux1.ToString();
					vbChannel6.Value = radio.Aux1;

					vbChannel7.RightLabel = radio.Aux2.ToString();
					vbChannel7.Value = radio.Aux2;

					vbChannel8.RightLabel = radio.Aux3.ToString();
					vbChannel8.Value = radio.Aux3;

					vbChannel5.LeftLabel = GearValueToFlightMode( radio.Gear );
				}
				else if(tcTabs.SelectedTab == tpRadioSetup)
				{
					if(RadioMode == RADIO_MODE.Mode2)	// North American
					{
						rsR_Left.SetParameters( radio.Rudd, radio.Thro );
						rsR_Right.SetParameters( radio.Aile, radio.Elev );

						vbR_LS_YValue.RightLabel = radio.Thro.ToString();
						vbR_LS_YValue.Value = radio.Thro;

						vbR_RS_YValue.RightLabel = radio.Elev.ToString();
						vbR_RS_YValue.Value = radio.Elev;

					}
					else if(RadioMode == RADIO_MODE.Mode1)	// European
					{
						rsR_Left.SetParameters( radio.Rudd, radio.Elev );
						rsR_Right.SetParameters( radio.Aile, radio.Thro );

						vbR_LS_YValue.RightLabel = radio.Elev.ToString();
						vbR_LS_YValue.Value = radio.Elev;

						vbR_RS_YValue.RightLabel = radio.Thro.ToString();
						vbR_RS_YValue.Value = radio.Thro;
					}

					vbR_LS_XValue.RightLabel = radio.Rudd.ToString();
					vbR_LS_XValue.Value = radio.Rudd;

					vbR_RS_XValue.RightLabel = radio.Aile.ToString();
					vbR_RS_XValue.Value = radio.Aile;


					vbR_Channel5.RightLabel = radio.Gear.ToString();
					vbR_Channel5.Value = radio.Gear;

					vbR_Channel6.RightLabel = radio.Aux1.ToString();
					vbR_Channel6.Value = radio.Aux1;

					vbR_Channel7.RightLabel = radio.Aux2.ToString();
					vbR_Channel7.Value = radio.Aux2;

					vbR_Channel8.RightLabel = radio.Aux3.ToString();
					vbR_Channel8.Value = radio.Aux3;

					vbR_Channel5.LeftLabel = GearValueToFlightMode( radio.Gear );
				}
				else if(tcTabs.SelectedTab == tpSystemSetup)
				{
					vbVoltage2.Value = radio.BatteryVolts;
					vbVoltage2.RightLabel = ((float)radio.BatteryVolts / 100.0f).ToString( "0.00" );
				}
			}

			if(bDebugChanged)
			{
				lblCycles.Text = string.Format( "CPU time (uS): {0} (min)   {1} (max)   {2} (avg)",
					debugData.MinCycles * 64/80, debugData.MaxCycles * 64/80, debugData.AvgCycles * 64/80 );

				tssFCVersion.Text = string.Format( "Firmware Version {0}.{1}", debugData.Version >> 8, debugData.Version & 255 );
			}

			if(bQuatChanged) {
				if(tcTabs.SelectedTab == tpStatus) {
					ocOrientation.Quat = q;

					Matrix m = new Matrix();
					m.From( q );

					double roll = Math.Asin( m.m[1,0] ) * (180.0/Math.PI);
					double pitch = Math.Asin( m.m[1,2] ) * (180.0 / Math.PI);
					double yaw = -Math.Atan2( m.m[2, 0], m.m[2, 2] ) * (180.0/Math.PI);

					int YawVal = ((int)yaw + 360) % 360;	// Make sure the number is in the range of 0 to 359 for the display

					aicAttitude.SetAttitudeIndicatorParameters( pitch, roll );
					aicHeading.SetHeadingIndicatorParameters( YawVal );
				}
				else if(tcTabs.SelectedTab == tpAccelCalibration) {
					ocAccelOrient.Quat = q;
				}
			}

			if(bTargetQuatChanged)
			{
				if(tcTabs.SelectedTab == tpStatus) {
					ocOrientation.Quat2 = cq;
				}
				else if(tcTabs.SelectedTab == tpAccelCalibration) {
					ocAccelOrient.Quat2 = cq;
				}
			}

			if(bMotorsChanged && tcTabs.SelectedTab == tpStatus) {
				vbFrontLeft.Value = motors.FL;
				vbFrontLeft.RightLabel = (motors.FL/8).ToString();

				vbFrontRight.Value = motors.FR;
				vbFrontRight.LeftLabel = (motors.FR/8).ToString();

				vbBackRight.Value = motors.BR;
				vbBackRight.LeftLabel = (motors.BR/8).ToString();

				vbBackLeft.Value = motors.BL;
				vbBackLeft.RightLabel = (motors.BL/8).ToString();
			}

			if(bComputedChanged)
			{
				vbPitchOut.RightLabel = computed.Pitch.ToString();
				vbPitchOut.Value = computed.Pitch;

				vbRollOut.RightLabel = computed.Roll.ToString();
				vbRollOut.Value = computed.Roll;

				vbYawOut.RightLabel = computed.Yaw.ToString();
				vbYawOut.Value = computed.Yaw;

				aicAltimeter.SetAlimeterParameters( (float)computed.AltiEst / 1000.0f );
				//aicHeading.SetHeadingIndicatorParameters( (computed.Yaw & ((1 << 17) - 1)) / (65536 / 180) );
			}

			if(bPrefsChanged) {
				ConfigureUIFromPreferences();
			}
		}


		void UpdateStatus()
		{
			CommStatus newStat = comm.Status;
			if(newStat != status)
			{
				status = newStat;
				string msg;

				switch(status)
				{
					case CommStatus.Initializing:
						msg = "Initializing";
						break;

					case CommStatus.NoDevice:
						msg = "No FTDI devices found";
						break;

					case CommStatus.NoElev8:
						msg = "No Elev8-FC connected";
						break;

					case CommStatus.Connected:
						msg = "Connected";
						break;

					default:
						msg = "";
						break;
				}

				tssStatusText.Text = msg;
			}
		}


		private void miRadioMode1_Click( object sender, EventArgs e )
		{
			SetRadioMode( RADIO_MODE.Mode1 );
		}

		private void miRadioMode2_Click( object sender, EventArgs e )
		{
			SetRadioMode( RADIO_MODE.Mode2 );
		}


		void SetRadioMode( RADIO_MODE mode )
		{
			RadioMode = mode;
			miRadioMode1.Checked = RadioMode == RADIO_MODE.Mode1;
			miRadioMode2.Checked = RadioMode == RADIO_MODE.Mode2;

			if(RadioMode == RADIO_MODE.Mode1)
			{
				vbLS_YValue.LeftLabel = "Elevator";
				vbRS_YValue.LeftLabel = "Throttle";
			}
			else if(RadioMode == RADIO_MODE.Mode2)
			{
				vbLS_YValue.LeftLabel = "Throttle";
				vbRS_YValue.LeftLabel = "Elevator";
			}

			Properties.Settings.Default.RadioMode = RadioMode.ToString();
			Properties.Settings.Default.Save();
		}

		void AttemptSetValue( TrackBar tb, int value )
		{
			if( value < tb.Minimum  || value > tb.Maximum ) return;
			tb.Value = value;
		}

		void AttemptSetValue( NumericUpDown ud, decimal value )
		{
			if(value < ud.Minimum || value > ud.Maximum) return;
			ud.Value = value;
		}

		void AttemptSetValue( HScrollBar hs, int value )
		{
			if(value >= hs.Minimum && value <= hs.Maximum) {
				hs.Value = value;
			}
		}


		void ConfigureUIFromPreferences()
		{
			InternalChange = true;

			cbReceiverType.SelectedIndex = prefs.ReceiverType;

			cbRev1.Checked = prefs.ThroScale < 0;
			cbRev2.Checked = prefs.AileScale < 0;
			cbRev3.Checked = prefs.ElevScale < 0;
			cbRev4.Checked = prefs.RuddScale < 0;
			cbRev5.Checked = prefs.GearScale < 0;
			cbRev6.Checked = prefs.Aux1Scale < 0;
			cbRev7.Checked = prefs.Aux2Scale < 0;
			cbRev8.Checked = prefs.Aux3Scale < 0;

			cbChannel1.SelectedIndex = prefs.ThroChannel;
			cbChannel2.SelectedIndex = prefs.AileChannel;
			cbChannel3.SelectedIndex = prefs.ElevChannel;
			cbChannel4.SelectedIndex = prefs.RuddChannel;
			cbChannel5.SelectedIndex = prefs.GearChannel;
			cbChannel6.SelectedIndex = prefs.Aux1Channel;
			cbChannel7.SelectedIndex = prefs.Aux2Channel;
			cbChannel8.SelectedIndex = prefs.Aux3Channel;

			if( prefs.PitchCorrectSin < -Math.PI * 0.5 || prefs.PitchCorrectSin > Math.PI * 0.5 ||
				prefs.PitchCorrectCos < -Math.PI * 0.5 || prefs.PitchCorrectCos > Math.PI * 0.5 )
			{
				prefs.PitchCorrectSin = 0.0f;
				prefs.PitchCorrectCos = 1.0f;
			}

			if( prefs.RollCorrectSin < -Math.PI * 0.5 || prefs.RollCorrectSin > Math.PI * 0.5 ||
				prefs.RollCorrectCos < -Math.PI * 0.5 || prefs.RollCorrectCos > Math.PI * 0.5)
			{
				prefs.RollCorrectSin = 0.0f;
				prefs.RollCorrectCos = 1.0f;
			}

			cbUseBatteryMonitor.Checked = prefs.UseBattMon == 1;


			double RollAngle = Math.Asin( prefs.RollCorrectSin );
			double PitchAngle = Math.Asin( prefs.PitchCorrectSin );

			lblAccelCalFinal.Text = string.Format( "{0}, {1}, {2}", prefs.AccelOffsetX, prefs.AccelOffsetY, prefs.AccelOffsetZ );

			AttemptSetValue( udRollCorrection, (decimal)(RollAngle * 180.0 / Math.PI) );
			AttemptSetValue( udPitchCorrection, (decimal)(PitchAngle * 180.0 / Math.PI) );

			int Value;


			float Source = prefs.AutoLevelRollPitch;
			Source = (Source * 2.0f) / ((float)Math.PI / 180.0f) * 1024.0f;
			AttemptSetValue( tbRollPitchAuto, (int)(Source + 0.5f) );

			Source = prefs.AutoLevelYawRate;
			Source = (Source * 2.0f) / ((float)Math.PI / 180.0f) * 1024.0f * 250.0f;
			AttemptSetValue( tbYawSpeedAuto, (int)(Source + 0.5f) / 10 );

			Source = prefs.ManualRollPitchRate;
			Source = (Source * 2.0f) / ((float)Math.PI / 180.0f) * 1024.0f * 250.0f;
			AttemptSetValue( tbRollPitchManual, (int)(Source * 20.0f + 0.5f) );

			Source = prefs.ManualYawRate;
			Source = (Source * 2.0f) / ((float)Math.PI / 180.0f) * 1024.0f * 250.0f;
			AttemptSetValue( tbYawSpeedManual, (int)(Source * 20.0f + 0.5f) );

			//Value = prefs.YawSpeed;
			//lblYawSpeedAuto.Text = ((float)Value / 64.0f).ToString( "0.00" );

			cbPitchRollLocked.Checked = (prefs.PitchRollLocked != 0);
			AttemptSetValue( hsPitchGain, prefs.PitchGain + 1 );
			AttemptSetValue( hsRollGain, prefs.RollGain + 1 );
			AttemptSetValue( hsYawGain, prefs.YawGain + 1 );
			AttemptSetValue( hsAscentGain, prefs.AscentGain + 1 );
			AttemptSetValue( hsAltiGain, prefs.AltiGain + 1 );
			cbEnableAdvanced.Checked = (prefs.UseAdvancedPID != 0);


			AttemptSetValue( udLowThrottle, (decimal)(prefs.MinThrottle / 8) );
			AttemptSetValue( udArmedLowThrottle, (decimal)(prefs.MinThrottleArmed / 8) );
			AttemptSetValue( udHighThrottle, (decimal)(prefs.MaxThrottle / 8) );
			AttemptSetValue( udTestThrottle, (decimal)(prefs.ThrottleTest / 8) );



			AttemptSetValue( udLowVoltageAlarmThreshold, (decimal)((float)prefs.LowVoltageAlarmThreshold / 100.0f) );
			AttemptSetValue( udVoltageOffset, (decimal)((float)prefs.VoltageOffset / 100.0f) );

			cbLowVoltageAlarm.Checked = (prefs.LowVoltageAlarm != 0);

			cbDisableMotors.Checked = (prefs.DisableMotors == 1);

			switch( prefs.ArmDelay )
			{
				default:
				case 250: cbArmingDelay.SelectedIndex = 0; break;	// 1 sec
				case 125: cbArmingDelay.SelectedIndex = 1; break;	// 1/2 sec
				case 62:  cbArmingDelay.SelectedIndex = 2; break;	// 1/4 sec
				case 0:   cbArmingDelay.SelectedIndex = 3; break;	// none
			}

			switch(prefs.DisarmDelay)
			{
				default:
				case 250: cbDisarmDelay.SelectedIndex = 0; break;	// 1 sec
				case 125: cbDisarmDelay.SelectedIndex = 1; break;	// 1/2 sec
				case 62:  cbDisarmDelay.SelectedIndex = 2; break;	// 1/4 sec
				case 0:   cbDisarmDelay.SelectedIndex = 3; break;	// none
			}

			Value = prefs.AccelCorrectionFilter;
			tbAccelCorrectionFilter.Value = Value;
			lblAccelCorrectionFilter.Text = ((float)Value / 256.0f).ToString( "0.000" );

			Value = prefs.ThrustCorrectionScale;
			tbThrustCorrection.Value = Value;
			//lblThrustCorrection.Text = ((float)Value / 256.0f).ToString( "0.000" );

			InternalChange = false;
		}


		private class ChannelData
		{
			public short min = 0;
			public short max = 0;
			public bool reverse = false;
			public int scale = 1024;
			public int center = 0;
		};

		ChannelData[] channelData = new ChannelData[8];
		int CalibrateControlsStep = 0;
		int CalibrateTimer = 0;


		private void btnCalibrate_Click( object sender, EventArgs e )
		{
			CalibrateControlsStep++;
			CalibrateTimer = 200;
		}

		private void btnControlReset_Click( object sender, EventArgs e )
		{
			for(int i = 0; i < 8; i++)
			{
				prefs.SetChannelIndex( i, (byte)i );
				prefs.SetChannelScale( i, 1024 );
				prefs.SetChannelCenter( i, 0 );
			}
			UpdateElev8Preferences();
		}

		void ResetAllScalesAndReverses()
		{
			SendCommand( "Rrad" );	// Reset radio channel scales
		}


		void CheckCalibrateControls()
		{
			switch(CalibrateControlsStep)
			{
				case 0:	return;	// Inactive

				case 1:
					for(int i = 0; i < 8; i++) {
						channelData[i] = new ChannelData();
					}
					ResetAllScalesAndReverses();
					CalibrateControlsStep++;
					return;

				case 2:
					tbCalibrateDocs.Visible = true;
					tbCalibrateDocs.Height = 140;
					/*tbCalibrateDocs.Lines = new string[] {
						"Move all sticks and levers to their\n",
						"full forward, or full right positions.\n",
						"(Controls above may respond incorrectly) - " + CalibrateTimer.ToString() };*/
                    			tbCalibrateDocs.Lines = new string[] {
						"Move both STICKS all the way to the RIGHT and UP, and all",
						"SWITCHES/KNOBS to their MAXIMUM value position. (1 or 2/clockwise)",
						"(Controls above may respond incorrectly) - " + CalibrateTimer.ToString() };

					if(CalibrateControlsStep == 200) {
						SystemSounds.Exclamation.Play();
					}

					if(CalibrateTimer < 10)
					{
						for(int i = 0; i < 8; i++)
						{
							if( Math.Abs(channelData[i].max) < Math.Abs( radio[i] ) ) {
								channelData[i].max = radio[i];
							}
						}
					}

					if( --CalibrateTimer == 0 )
					{
						CalibrateControlsStep++;
						CalibrateTimer = 200;
					}
					break;

				case 3:
					/*tbCalibrateDocs.Lines = new string[] { "Move all sticks and levers to\n",
						"their full back, or full left positions.\n",
						CalibrateTimer.ToString() };*/
                    			tbCalibrateDocs.Lines = new string[] { "Move both STICKS all the way to the LEFT and DOWN, and all",
						"SWITCHES/KNOBS to their MINIMUM value position (0/counter-clockwise).",
						CalibrateTimer.ToString() };


					if(CalibrateControlsStep == 200) {
						SystemSounds.Exclamation.Play();
					}

					if(CalibrateTimer < 10)
					{
						for(int i = 0; i < 8; i++)
						{
							if( Math.Abs(channelData[i].min) < Math.Abs( radio[i] ) ) {
								channelData[i].min = radio[i];
							}
						}
					}

					if( --CalibrateTimer == 0 )
					{
						CalibrateControlsStep++;
						CalibrateTimer = 200;
					}
					break;

				case 4:
					CalibrateControlsStep = 0;
					tbCalibrateDocs.Text = "";
					tbCalibrateDocs.Visible = false;


					// figure out reverses
					for(int i = 0; i < 8; i++)
					{
						if(channelData[i].min > channelData[i].max) {
							channelData[i].reverse = true;	// Needs to be reversed with respect to what's already stored
						}

						int range = Math.Abs( channelData[i].min - channelData[i].max );
						channelData[i].scale = (int)((1.0f / ((float)range / 2048.0f)) * 1024.0f);
						channelData[i].center = (channelData[i].min + channelData[i].max) / 2;
						if(channelData[i].reverse) channelData[i].scale *= -1;
					}

					// apply to prefs
					prefs.ThroScale = (short)channelData[0].scale;
					prefs.AileScale = (short)channelData[1].scale;
					prefs.ElevScale = (short)channelData[2].scale;
					prefs.RuddScale = (short)channelData[3].scale;
					prefs.GearScale = (short)channelData[4].scale;
					prefs.Aux1Scale = (short)channelData[5].scale;
					prefs.Aux2Scale = (short)channelData[6].scale;
					prefs.Aux3Scale = (short)channelData[7].scale;


					prefs.ThroCenter = (short)channelData[0].center;
					prefs.AileCenter = (short)channelData[1].center;
					prefs.ElevCenter = (short)channelData[2].center;
					prefs.RuddCenter = (short)channelData[3].center;
					prefs.GearCenter = (short)channelData[4].center;
					prefs.Aux1Center = (short)channelData[5].center;
					prefs.Aux2Center = (short)channelData[6].center;
					prefs.Aux3Center = (short)channelData[7].center;


					UpdateElev8Preferences();
					break;
			}
		}


		private void cbChannel_SelectedIndexChanged( object sender, EventArgs e )
		{
			if(InternalChange) return;
			ComboBox cbSender = sender as ComboBox;

			string tagIndexString = cbSender.Tag as string;
			int tagIndex = 0;
			if(tagIndexString != null)
				tagIndex = int.Parse( tagIndexString );

			int channelIndex = cbSender.SelectedIndex;
			if(tagIndex > 0 && channelIndex >=0 && channelIndex < 8) {
				prefs.SetChannelIndex( tagIndex-1, (byte)channelIndex );
				UpdateElev8Preferences();
			}
		}

		private void cbRev_CheckedChanged( object sender, EventArgs e )
		{
			if(InternalChange) return;
			CheckBox cbSender = sender as CheckBox;

			string tagIndexString = cbSender.Tag as string;
			int tagIndex = 0;
			if(tagIndexString != null)
				tagIndex = int.Parse( tagIndexString );

			if(tagIndex > 0)
			{
				short scale = prefs.GetChannelScale( tagIndex-1 );
				scale *= -1;

				prefs.SetChannelScale( tagIndex-1 , scale );
				UpdateElev8Preferences();
			}
		}


		void UpdateElev8Preferences()
		{
			// Send prefs
			prefs.Checksum = prefs.CalculateChecksum();
			byte[] prefBytes = prefs.ToBytes();

			SendCommand( "UPrf" );	// Update preferences

			Thread.Sleep( 10 );

			// Have to slow this down a little during transmission because the buffer
			// on the other end is small to save ram, and we don't have flow control
			for(int i = 0; i < prefBytes.Length; i++)
			{
				txBuffer[0] = prefBytes[i];
				comm.Send( txBuffer, 1 );
				Thread.Sleep( 5 );
			}

			// Query prefs (forces to be applied to UI)
			SendCommand( "QPRF" );
		}


		private void TestMotor( int MotorIndex )
		{
			if(comm.Connected)
			{
				CancelThrottleCalibration();	// just in case

				SendCommand( string.Format( "M{0}t{0}", MotorIndex+1) );	// Becomes M1t1, M2t2, etc..
			}
		}


		private void btnMotor1_MouseDown( object sender, MouseEventArgs e )
		{
			TestMotor( 0 );
		}

		private void btnMotor2_MouseDown( object sender, MouseEventArgs e )
		{
			TestMotor( 1 );
		}

		private void btnMotor3_MouseDown( object sender, MouseEventArgs e )
		{
			TestMotor( 2 );
		}

		private void btnMotor4_MouseDown( object sender, MouseEventArgs e )
		{
			TestMotor( 3 );
		}


		private void btnBeeper_Click( object sender, EventArgs e )
		{
			TestMotor( 4 );
		}

		private void btnLED_Click( object sender, EventArgs e )
		{
			TestMotor( 5 );
		}


		private void btnThrottleCalibrate_Click( object sender, EventArgs e )
		{
			switch(CalibrationCycle)
			{
				case 0:
					TestMotor( 6 );
					lblCalibrateDocs.Text = "Throttle calibration has started.  Be sure your flight battery is UNPLUGGED, then press the Throttle Calibration button again.  (Click any other button to cancel)";
					CalibrationCycle = 1;

					// TODO - Should probably disable all other buttons, and make an abort button visible
					break;

				case 1:
					txBuffer[0] = (byte)0xFF;
					comm.Send( txBuffer, 1 );
					//lblCalibrateDocs.Text = "Plug in your flight battery and wait for the ESCs to beep twice, then press the Throttle Calibration button again";
					lblCalibrateDocs.Text = "Plug in your flight battery and wait for the ESCs to stop beeping (about 5 seconds), then press the Throttle Calibration button again";
					CalibrationCycle = 2;
					break;

				case 2:
					txBuffer[0] = 0;		// Finish
					comm.Send( txBuffer, 1 );
					//lblCalibrateDocs.Text = "Calibration complete";
					lblCalibrateDocs.Text = "Once the ESCs stop beeping (about 5 seconds), calibration is complete and your may remove the flight battery";
					lblCalibrateDocs.Update();
					System.Threading.Thread.Sleep( 1000 * 3 );
					lblCalibrateDocs.Text = "";
					CalibrationCycle = 0;

					// TODO: Re-enable all other buttons, hide the abort button
					break;
			}
		}

		private void CancelThrottleCalibration()
		{
			if(CalibrationCycle == 0) return;

			txBuffer[0] = 0;		// Escape from throttle setting
			comm.Send( txBuffer, 1 );
			lblCalibrateDocs.Text = "";
			CalibrationCycle = 0;
		}


		private void cbGraphLegend_CheckedChanged( object sender, EventArgs e )
		{
			CheckBox cbSender = sender as CheckBox;
			string tagString = cbSender.Tag as string;
			if(tagString == null) return;

			int tagIndex = int.Parse( tagString );
			if(tagIndex < 1 || tagIndex > 15) return;

			bool newState = cbSender.Checked;
			graphSources[tagIndex - 1].Active = newState;
			plotSensors.Refresh();
		}


		private void btnResetCalib_Click( object sender, EventArgs e )
		{
			lfGraph.Reset();
		}


		private void btnUploadCalibration_Click( object sender, EventArgs e )
		{
			prefs.DriftScaleX = (int)Math.Round( 1.0 / lfGraph.dSlope.x );
			prefs.DriftScaleY = (int)Math.Round( 1.0 / lfGraph.dSlope.y );
			prefs.DriftScaleZ = (int)Math.Round( 1.0 / lfGraph.dSlope.z );
			prefs.DriftOffsetX = (int)Math.Round( lfGraph.dIntercept.x );
			prefs.DriftOffsetY = (int)Math.Round( lfGraph.dIntercept.y );
			prefs.DriftOffsetZ = (int)Math.Round( lfGraph.dIntercept.z );

			UpdateElev8Preferences();
		}


		private void tcTabs_SelectedIndexChanged( object sender, EventArgs e )
		{
			Mode newMode;
			CancelThrottleCalibration();	// just in case

			if(tcTabs.SelectedTab == tpGyroCalibration)
			{
				newMode = Mode.GyroCalibration;
			}
			else if(tcTabs.SelectedTab == tpAccelCalibration)
			{
				newMode = Mode.AccelCalibration;
			}
			else
			{
				newMode = Mode.SensorTest;
			}

			if(newMode == currentMode) {
				return;
			}

			// If we're switching OUT of one of the calibration modes
			// tell the flight controller to revert back to its drift-compensated settings
			if(currentMode == Mode.GyroCalibration)
			{
				SendCommand( "RGyr" );	// revert previous gyro calibration values
			}
			else if(currentMode == Mode.AccelCalibration)
			{
				SendCommand( "RAcl");	// revert previous accel calibration values
			}

			if(newMode == Mode.GyroCalibration)
			{
				SendCommand( "ZrGr" );	// zero gyro calibration settings
			}
			else if(newMode == Mode.AccelCalibration)
			{
				SendCommand( "ZeAc" );	// zero accelerometer calibration settings
			}

			currentMode = newMode;
		}


		// Accelerometer calibration holding variables
		float[] accXCal = new float[4];
		float[] accYCal = new float[4];
		float[] accZCal = new float[4];


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

			prefs.AccelOffsetX = ax;
			prefs.AccelOffsetY = ay;
			prefs.AccelOffsetZ = az;

			UpdateElev8Preferences();
		}


		private void btnUploadAngleCorrection_Click( object sender, EventArgs e )
		{
			// Upload calibration data

			double rollOffset = (float)((double)udRollCorrection.Value * Math.PI / 180.0);
			double pitchOffset = (float)((double)udPitchCorrection.Value * Math.PI / 180.0);

			prefs.RollCorrectSin = (float)Math.Sin( rollOffset );
			prefs.RollCorrectCos = (float)Math.Cos( rollOffset );
			prefs.PitchCorrectSin = (float)Math.Sin( pitchOffset );
			prefs.PitchCorrectCos = (float)Math.Cos( pitchOffset );

			UpdateElev8Preferences();
		}


		private void tbRollPitchAngle_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbRollPitchAuto.Value;
			float Scale = ((float)Value / 1024.0f) * ((float)Math.PI/180.0f) * 0.5f;

			lblRollPitchAngle.Text = Value.ToString() + " deg";
		}

		private void tbYawSpeedAuto_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbYawSpeedAuto.Value * 10;
			float Scale = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			lblYawSpeedAuto.Text = Value.ToString() + " deg/s";
		}


		private void tbRollPitchManual_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbRollPitchManual.Value * 10;
			float Scale = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			lblRollPitchManual.Text = Value.ToString() + " deg/s";
		}

		private void tbYawSpeedManual_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbYawSpeedManual.Value * 10;
			float Scale = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			lblYawSpeedManual.Text = Value.ToString() + " deg/s";
		}


		private void tbAccelCorrectionFilter_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbAccelCorrectionFilter.Value;
			lblAccelCorrectionFilter.Text = ((float)Value / 256.0f).ToString( "0.000" );
		}

		private void tbThrustCorrection_ValueChanged( object sender, EventArgs e )
		{
			int Value = tbThrustCorrection.Value;
			lblThrustCorrection.Text = ((float)Value / 256.0f).ToString( "0.000" );
		}


		private void btnUploadRollPitch_Click( object sender, EventArgs e )
		{
			int Value = tbRollPitchAuto.Value;
			float Rate = ((float)Value / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			prefs.AutoLevelRollPitch = Rate;

			Value = tbYawSpeedAuto.Value * 10;
			Rate = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			prefs.AutoLevelYawRate = Rate;

			Value = tbRollPitchManual.Value * 10;
			Rate = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			prefs.ManualRollPitchRate = Rate;

			Value = tbYawSpeedManual.Value * 10;
			Rate = (((float)Value / 250.0f) / 1024.0f) * ((float)Math.PI / 180.0f) * 0.5f;
			prefs.ManualYawRate = Rate;

			prefs.AccelCorrectionFilter = (short)tbAccelCorrectionFilter.Value;
			prefs.ThrustCorrectionScale = (short)tbThrustCorrection.Value;

			UpdateElev8Preferences();
		}


		short[] DelayTable = { 250, 125, 62, 0 };

		private void btnUploadThrottle_Click( object sender, EventArgs e )
		{
			prefs.MinThrottle = (short)(udLowThrottle.Value * 8);
			prefs.MinThrottleArmed = (short)(udArmedLowThrottle.Value * 8);
			prefs.ThrottleTest = (short)(udTestThrottle.Value * 8);
			prefs.MaxThrottle = (short)(udHighThrottle.Value * 8);

			prefs.UseBattMon = cbUseBatteryMonitor.Checked ? (byte)1 : (byte)0;
			prefs.LowVoltageAlarmThreshold = (short)(udLowVoltageAlarmThreshold.Value * 100);
			prefs.VoltageOffset = (short)(udVoltageOffset.Value * 100);
			prefs.LowVoltageAlarm = (byte)(cbLowVoltageAlarm.Checked ? 1 : 0);

			prefs.ArmDelay = DelayTable[cbArmingDelay.SelectedIndex];
			prefs.DisarmDelay = DelayTable[cbDisarmDelay.SelectedIndex];

			prefs.DisableMotors = (byte)(cbDisableMotors.Checked ? 1 : 0);

			UpdateElev8Preferences();
		}


		private void cbDisableMotors_CheckedChanged( object sender, EventArgs e )
		{
			if(cbDisableMotors.Checked)
			{
				cbDisableMotors.BackColor = Color.Pink;
			}
			else
			{
				cbDisableMotors.BackColor = Color.Transparent;
			}
		}


		private void btnFactoryDefaultPrefs_Click( object sender, EventArgs e )
		{
			SendCommand( "WIPE" );	// Reset prefs
			SendCommand( "QPRF" );	// Query prefs (forces to be applied to UI)
		}

		private void cbReceiverType_SelectedIndexChanged( object sender, EventArgs e )
		{
			prefs.ReceiverType = (byte)cbReceiverType.SelectedIndex;
		}


		private void hsPitchGain_ValueChanged( object sender, EventArgs e )
		{
			if(cbPitchRollLocked.Checked)
			{
				if(hsRollGain.Value != hsPitchGain.Value) {
					hsRollGain.Value = hsPitchGain.Value;
				}
			}
			lblPitchGain.Text = hsPitchGain.Value.ToString();
		}


		private void hsRollGain_ValueChanged( object sender, EventArgs e )
		{
			if(cbPitchRollLocked.Checked)
			{
				if(hsPitchGain.Value != hsRollGain.Value) {
					hsPitchGain.Value = hsRollGain.Value;
				}
			}

			lblRollGain.Text = hsRollGain.Value.ToString();
		}


		private void cbPitchRollLocked_CheckedChanged( object sender, EventArgs e )
		{
			if(cbPitchRollLocked.Checked)
			{
				if(hsRollGain.Value != hsPitchGain.Value) {
					hsRollGain.Value = hsPitchGain.Value;
				}
			}
		}

		private void hsYawGain_ValueChanged( object sender, EventArgs e )
		{
			lblYawGain.Text = hsYawGain.Value.ToString();
		}

		private void hsAscentGain_ValueChanged( object sender, EventArgs e )
		{
			lblAscentGain.Text = hsAscentGain.Value.ToString();
		}

		private void hsAltiGain_ValueChanged( object sender, EventArgs e )
		{
			lblAltiGain.Text = hsAltiGain.Value.ToString();
		}

		private void btnUploadFlightChanges_Click( object sender, EventArgs e )
		{
			// Pull the values from the flight controls page
			prefs.PitchRollLocked = cbPitchRollLocked.Checked ? (byte)1 : (byte)0;
			prefs.UseAdvancedPID = cbEnableAdvanced.Checked ? (byte)1 : (byte)0;

			prefs.PitchGain = (byte)(hsPitchGain.Value - 1);
			prefs.RollGain = (byte)(hsRollGain.Value - 1);
			prefs.YawGain = (byte)(hsYawGain.Value - 1);
			prefs.AscentGain = (byte)(hsAscentGain.Value - 1);
			prefs.AltiGain = (byte)(hsAltiGain.Value - 1);

			// Apply the prefs to the elev-8
			UpdateElev8Preferences();
		}


		private void aboutToolStripMenuItem_Click( object sender, EventArgs e )
		{
			AboutBox ab = new AboutBox();
			ab.ShowDialog();
		}
	}
}
