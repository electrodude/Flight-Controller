cmake_minimum_required(VERSION 3.0)

create_executable(Elev8
    Beep
    Eeprom
    Elev8-Main
    F32
    F32_driver.spin
    IntPID
    QuatIMU
    pst.spin
    RC
    RC_driver.spin
    SBUS
    SBUS_driver.spin
    Sensors
    Sensors_driver.spin
    Servo32_HighRes
    Servo32_HighRes_driver.spin
    Settings
    )
