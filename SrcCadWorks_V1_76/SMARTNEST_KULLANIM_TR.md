# SMARTNEST - SrcCadWorks kaynak kod ön sürümü

Bu kaynak paketi V1.74 temel paket üzerine SMARTNEST yerleşim çekirdeğini ekler.

## Ayarlar

`SmartNestSettings` varsayılanları:
- Sac: 1000 x 2000 mm
- Parçalar arası aralık: **5 mm**
- Sac kenar payı: 5 mm
- Kerf: 0 mm

Bütün değerler kod üzerinden değiştirilebilir. Yaygın sac ebatları 1000x2000, 1250x2500 ve 1500x3000 mm olarak ayarlanabilir.

## Çekirdek API

- `SmartNestPart`: parça adı, adet, dış kontur, iç konturlar ve döndürme izni.
- `SmartNestEngine.Nest(parts, settings)`: yerleşim hesaplar.
- `SmartNestEngine.WriteDxf(path, result)`: yerleşim dış konturlarını DXF olarak yazar.
- `SmartNestResult`: koordinatlar, sac sayısı ve kullanım yüzdesi.

Koordinatlar milimetredir. Dış kontur en az üç noktalı, kapalı poligon olarak temsil edilir (son noktanın ilk noktayla tekrar edilmesi gerekmez).

## Durum

Bu paket çekirdek ön sürümdür; mevcut DXF dışa aktarma ekranına henüz bağlanmamıştır. Açık DXF çizgilerinin tespiti, DXF entity parser/adaptörü, iç deliklerin çıktıya taşınması, gerçek geometrik offset ve Windows/SOLIDWORKS derlemesi tamamlanmadan üretim kesimine gönderilmemelidir.


## V1.76 montaj tarama ve DXF kontur davranışı

SMARTNEST montaj kaynağı için Lazer Hazırla modülünde kullanılan `AssemblyScanner` ve `PartAnalyzer` zincirini kullanır. Alt montajların tüm parça occurrence'ları taranır; suppressed bileşenler hariç tutulur; aynı dosya+konfigürasyonun montaj adedi korunur. Yalnız analyzer'ın gerçek sac levha olarak döndürdüğü SLDPRT kayıtları açınım aktarımına gider.

DXF dış konturu için `LWPOLYLINE`, `POLYLINE`, bulge yayları, `CIRCLE`, `ELLIPSE` ve uçları birleştirilebilen `LINE`/`ARC`/fit-point `SPLINE` segmentleri okunur. DXF ENTITIES bölümünde kapalı döngü bulunamazsa bulunan entity türleri hata mesajına eklenir.
