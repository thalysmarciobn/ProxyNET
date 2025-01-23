# Projeto Proxy Forward

## Visão Geral

Este projeto implementa um servidor proxy TCP simples que encaminha dados entre um cliente e um servidor de destino. É projetado para ser leve e eficiente, utilizando programação assíncrona para lidar com múltiplas conexões simultaneamente. O servidor proxy escuta conexões TCP de entrada e encaminha os dados para um endpoint de destino especificado, sendo útil para diversas tarefas de rede, como depuração, monitoramento ou anonimização de tráfego.

## Funcionalidades

- **Manipulação Assíncrona**: Utiliza programação assíncrona para lidar com múltiplas conexões de clientes sem bloqueio.
- **Endpoints Configuráveis**: O endereço de escuta do servidor proxy e o endereço do servidor de destino podem ser configurados através de um arquivo JSON.
- **Registro de Logs**: Integração de logging usando Serilog para fornecer informações detalhadas sobre as operações do proxy e quaisquer erros encontrados.
- **Encaminhamento de Dados**: Encaminha dados de forma eficiente entre o cliente e o servidor de destino, com tratamento de erros para exceções de socket.

## Tecnologias Utilizadas

- **C#**: A linguagem de programação principal utilizada para a implementação.
- **.NET**: O framework utilizado para construir a aplicação.
- **Serilog**: Uma biblioteca de logging para .NET que fornece capacidades de logging estruturado.
- **TCP/IP**: O protocolo subjacente utilizado para comunicação em rede.

## Começando

### Pré-requisitos

- SDK do .NET (versão 9.0 ou superior)
- Um editor de código (ex: Visual Studio, Visual Studio Code)

### Instalação

1. Clone o repositório:

   ```bash
   git clone https://github.com/thalysmarciobn/ProxyNET.git

![Diagrama Proxy](https://github.com/thalysmarciobn/ProxyNET/blob/main/diag.png?raw=true "Diagrama")