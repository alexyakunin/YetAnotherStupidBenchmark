#!/bin/bash

PATH=$HOME/dotnet:$PATH
DOTNET_ROOT=$HOME/dotnet
dotnet run -c Release -- "$@"
