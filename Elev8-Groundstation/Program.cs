﻿/*
  Elev8 GroundStation

  Copyright 2015 Parallax Inc

  This work is licensed under a Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.
  http://creativecommons.org/licenses/by-nc-sa/4.0/

  Written by Jason Dorie
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Elev8
{
	static class Program
	{
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault( false );

			Application.ThreadException += new System.Threading.ThreadExceptionEventHandler( Application_ThreadException );

			try
			{
				Application.Run( new MainForm() );
			}

			catch(Exception e)
			{
				ExceptionDump( e );
			}
		}


		static void Application_ThreadException( object sender, System.Threading.ThreadExceptionEventArgs e )
		{
			ExceptionDump( e.Exception );
		}


		static void ExceptionDump( Exception e )
		{
			Clipboard.SetText( e.StackTrace );
			MessageBox.Show( e.StackTrace, "Crash! - stack trace below" );
		}
	}
}
