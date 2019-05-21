cmake_minimum_required(VERSION 3.14)
project(Cpp)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Ofast -fomit-frame-pointer -march=native -mtune=native -funroll-loops -Wno-shift-count-overflow" )

add_executable(Cpp main.cpp)
