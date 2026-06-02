# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Switch to root user to install packages
USER root

# Hack to Allow Tesseract to work
RUN apt-get update \
    && apt-get install -y --allow-unauthenticated \
        libleptonica-dev \
        libtesseract-dev \
        ghostscript \
    && rm -rf /var/lib/apt/lists/*

RUN ln -s /usr/lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so

WORKDIR /app/x64

RUN ln -s /usr/lib/x86_64-linux-gnu/liblept.so /app/x64/libleptonica-1.82.0.so
RUN ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so /app/x64/libtesseract50.so
#RUN ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so.4 /app/x64/libtesseract50.so

# Clean up to reduce image size
RUN apt-get clean && rm -rf /var/lib/apt/lists/*
# Hack to Allow Tesseract to work

USER app
# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["LlamaIntegrationAPI/LlamaIntegrationAPI.csproj", "LlamaIntegrationAPI/"]
RUN dotnet restore "./LlamaIntegrationAPI/LlamaIntegrationAPI.csproj"
COPY . .
WORKDIR "/src/LlamaIntegrationAPI"
RUN dotnet build "./LlamaIntegrationAPI.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LlamaIntegrationAPI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LlamaIntegrationAPI.dll"]