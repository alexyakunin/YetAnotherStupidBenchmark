#!/bin/bash
CORECLR_PATH=$HOME/Projects/coreclr

dotnet publish -c Release

pushd ./bin/Relase/net5.0/linux-x64/publish
cp -rT $CORECLR_PATH/bin/Product/Linux.x64.Debug .
#cp $CORECLR_PATH/bin/Product/Linux.x64.Debug/libclrjit.so .

#export COMPlus_JitDump=ProcessSpanUnsafe2
export COMPlus_JitDisasm=ProcessSpanUnsafe2
export COMPlus_JitDiffableDasm=1
dotnet YetAnotherStupidBenchmark.dll
popd