@echo off
set PATH=C:\MinGW\bin;%PATH%
pushd cmake-build-release
rem mingw32-make
Cpp.exe %*
popd
