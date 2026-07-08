FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app
ARG COMMIT

# Espelha a MESMA estrutura que os ProjectReference esperam (../libplanet/src/...)
COPY ./libplanet/Directory.Build.props ./libplanet/
COPY ./libplanet/Menees.Analyzers.Settings.xml ./libplanet/
COPY ./libplanet/stylecop.json ./libplanet/
COPY ./libplanet/src/Directory.Build.props ./libplanet/src/
COPY ./libplanet/src/Libplanet/Libplanet.csproj ./libplanet/src/Libplanet/
COPY ./libplanet/src/Libplanet.RocksDBStore/Libplanet.RocksDBStore.csproj ./libplanet/src/Libplanet.RocksDBStore/
COPY ./libplanet/src/Libplanet.Store/Libplanet.Store.csproj ./libplanet/src/Libplanet.Store/
COPY ./NineChronicles.Snapshot/NineChronicles.Snapshot.csproj ./NineChronicles.Snapshot/

# Restaura a partir do projeto raiz: ele puxa os 3 ProjectReference sozinho
RUN dotnet restore NineChronicles.Snapshot/NineChronicles.Snapshot.csproj

# Copy everything else and build
COPY . ./

# Falha cedo e claro se o submódulo vier vazio
RUN test -f libplanet/src/Libplanet.Store/Libplanet.Store.csproj \
    || (echo "❌ submódulo libplanet incompleto: git submodule update --init --recursive" && exit 1)

RUN dotnet publish NineChronicles.Snapshot/NineChronicles.Snapshot.csproj \
    -c Release \
    -r linux-x64 \
    -o out \
    --self-contained true

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN apt-get update && apt-get install -y \
    libc6-dev \
    librocksdb-dev \
    libsnappy-dev \
    liblz4-dev \
    libzstd-dev
COPY --from=build-env /app/out .

VOLUME /data

ENTRYPOINT ["dotnet", "NineChronicles.Snapshot.dll"]
