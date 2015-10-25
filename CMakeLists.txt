cmake_minimum_required(VERSION 3.0)
find_package(PropWare 2.0 REQUIRED)

project(Flight-Controller C CXX SPIN2DAT)

set(MODEL cmm)
set(COMMON_FLAGS "-Os")

add_subdirectory(Firmware-C)
