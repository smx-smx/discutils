#!/bin/bash
if [ -z "$1" ] || [ -z "$2" ]; then
	echo "Usage: $0 [sln file][framework version]"
	exit 1
fi

sln="$1"
framework=$(printf '%q' "$2")

grep "^Project" "${sln}" | while read -r project; do
	echo "${project}" |
	cut -d "," -f2 |
	sed 's/"//g;s/\\/\//g' |
	awk '{print $1}' |
	while read -r csproj; do
		#grep TargetFrameworkVersion "${csproj}" | 
		echo "Patching ${csproj}..."
		perl -pe "s/(\<TargetFrameworkVersion\>)(.*?)(\<\/TargetFrameworkVersion\>)/\1${framework}\3/g" -i "${csproj}"
	done
done
