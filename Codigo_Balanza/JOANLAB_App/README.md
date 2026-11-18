# Aplicación de lectura JOANLAB FA1204N

Aplicación Windows para recibir las mediciones que el ESP32-S3 entrega por un
puerto COM. Permite:

- seleccionar y actualizar el puerto COM;
- seleccionar el baud rate (predeterminado: 115200);
- visualizar la última lectura y la trama original;
- asignar un nombre a cada set de datos adquirido;
- definir el intervalo de muestreo entre 100 ms y 1 hora;
- iniciar y detener el muestreo;
- guardar automáticamente las muestras en CSV;
- almacenar los CSV en la carpeta independiente `Datos` junto al ejecutable;
- conservar fecha/hora de recepción y fecha/hora de cada muestra.

## Ejecutar

Abra `JOANLAB_Balanza.exe`. En la prueba realizada el ESP32-S3 fue detectado en
`COM25`, aunque la aplicación permite seleccionar cualquier puerto disponible.

1. Seleccione el puerto y deje `115200` baudios.
2. Presione **Conectar**.
3. Escriba el nombre del set de datos e indique el intervalo de muestreo.
4. Presione **Iniciar muestreo**. La aplicación crea automáticamente la carpeta
   `Datos` junto al ejecutable y guarda allí el nuevo CSV.

Cada vez que se inicia un muestreo se crea un archivo independiente con el
nombre del set y la fecha/hora de inicio. Por ejemplo:

```text
Datos\Ensayo_control_20261118_153000.csv
```

El nombre del set también se incluye como una columna dentro del CSV.

La aplicación reconoce tramas como:

```text
E *-    12.7  mg
```

El valor almacenado será `12.7`, la unidad `mg` y también se conservará la
trama completa.

## Volver a compilar

Desde PowerShell, dentro de esta carpeta:

```powershell
powershell -ExecutionPolicy Bypass -File .\compilar.ps1
```

La compilación usa las bibliotecas de Windows/.NET Framework incluidas en el
sistema y no requiere instalar Python ni paquetes adicionales.
