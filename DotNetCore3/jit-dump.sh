#!/bin/bash

PATH=$HOME/dotnet:$PATH
DOTNET_ROOT=$HOME/dotnet
CORECLR_PATH=$HOME/Projects/coreclr

dotnet publish -c Release

pushd ./bin/Debug/netcoreapp3.0/linux-x64/publish
cp -rT $CORECLR_PATH/bin/Product/Linux.x64.Debug .
#cp $CORECLR_PATH/bin/Product/Linux.x64.Debug/libclrjit.so .

#export COMPlus_JitDump=ProcessSpanUnsafe2
export COMPlus_JitDisasm=ProcessSpanUnsafe2
export COMPlus_JitDiffableDasm=1
dotnet YetAnotherStupidBenchmark.dll
popd