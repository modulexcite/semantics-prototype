#!/bin/bash
set -e

# if you don't have nuget, do
#   curl -L "https://nuget.org/nuget.exe" -o /usr/bin/nuget.exe
# or equivalent. then chmod +x it.
# you might need to install the right certificates with 
#   sudo mozroots --import --machine --sync
# first. naturally you need mono.
# once you have nuget you can update it to the latest version with
#   nuget.exe update -self
# and you should see output like
#   Checking for updates from https://www.nuget.org/api/v2/.
#   Currently running NuGet.exe 2.8.6.

# for some reason 'mono nuget.exe' won't find nuget in /usr/bin :linux: :iiam:
NUGET=`which nuget.exe`

# nuget might do this for us? but why leave it to chance
mkdir -p ./packages/

# we have to explicitly specify the packages directory, since nuget
#  usually installs into . unless it's doing a restore for a .sln file
mono $NUGET restore -PackagesDirectory ./packages/

# symlinks for convenient importing
mkdir -p ./libs/

FPARSEC=`realpath ./packages/FParsec.*/lib/net40-client/`
ln -f -s $FPARSEC libs/FParsec

FSHARPCORE=`realpath ./packages/FSharp.Core.*/lib/net40/`
ln -f -s $FSHARPCORE libs/FSharp.Core