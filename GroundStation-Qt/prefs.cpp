/*
  This file is part of the ELEV-8 Flight Controller Firmware
  for Parallax part #80204, Revision A
  
  Copyright 2015 Parallax Incorporated

  ELEV-8 Flight Controller Firmware is free software: you can redistribute it and/or modify it
  under the terms of the GNU General Public License as published by the Free Software Foundation, 
  either version 3 of the License, or (at your option) any later version.

  ELEV-8 Flight Controller Firmware is distributed in the hope that it will be useful, but 
  WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or 
  FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with the ELEV-8 Flight Controller Firmware.  If not, see <http://www.gnu.org/licenses/>.
  
  Written by Jason Dorie

  Prefs - User prefs storage for Elev8-FC
*/

#include <string.h>   // for memset()
#include "prefs.h"


PREFS Prefs;


int Prefs_CalculateChecksum( unsigned int * PrefsStruct , int size )
{
  unsigned int r = 0x55555555;            //Start with a strange, known value
  for( int i=0; i < (size/4)-1; i++ )
  {
    r = (r << 7) | (r >> (32-7));
	r = r ^ PrefsStruct[i];     //Jumble the bits, XOR in the prefs value
  }    
  return (int)r;
}


int Prefs_CalculateChecksum( PREFS & PrefsStruct )
{
	return Prefs_CalculateChecksum( (unsigned int *)&PrefsStruct , sizeof(PrefsStruct) );
}
