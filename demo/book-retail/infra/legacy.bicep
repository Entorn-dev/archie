param location string = resourceGroup().location

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'bookretaillegacydemo'
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
}

resource fulfilment 'Microsoft.Web/sites@2023-12-01' = {
  name: 'book-retail-legacy-fulfilment'
  location: location
  kind: 'functionapp'
  properties: {
    httpsOnly: true
    serverFarmId: 'manual-demo-plan'
  }
}
