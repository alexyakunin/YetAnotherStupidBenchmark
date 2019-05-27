@echo off
set PATH=%PATH%;C:\MinGW\bin
pushd cmake-build-release
Cpp.exe %*
popd
