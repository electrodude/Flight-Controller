cmake_minimum_required(VERSION 3.0)

create_executable(Elev8
    battery
    beep
    commlink
    eeprom
    elev8-main
    f32
    f32_driver.spin
    intpid
    laserrange
    prefs
    quatimu
    pst.spin
    rc
    rc_driver_ppm.spin
    rc_driver_v3.spin
    remote_rx_driver.spin
    sbus
    sbus_driver.spin
    sensors
    sensors_driver.spin
    serial_4x.cpp
    serial_4x_driver.spin
    servo32_highres
    servo32_highres_driver.spin
    )
