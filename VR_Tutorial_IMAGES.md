# VR_Tutorial: 3D Snapshots
This is a guide to convert a rendered image from VMD into a 3D format that can be viewed in VR. 

## You will need:

* StereoPhotoMaker (only operates on Windows)

* The mobile app for your VR Headset, i.e Meta Horizon

* Sidequest

* Pigasus VR

* & VMD!

## Render
1. Load your desired 3D model into VMD.

2. Once loaded, make sure each graphical representation has the material **AOChalky** applied. 

3. File >  Render... > Select **Tachyon (internal,in-memory rendering)**

4. Convert tga file to a jpg through [3rd party website/app.](https://convertio.co/tga-jpg/) 

## Create .MPO File

### MPO stands for Multi-Picture Object, made up of two jpg images. In order to construct this file, we will need to use a depth-map to make the image compatible with stereoscopic displays. 

### Before completing the step below, it is necessary to install the _Mannequin Challenge_ google ai onto your Windows computer. This is what will generate the depth map. [Click here for instructions on how to download](https://stereo.jpn.org/jpn/stphmkr/google/indexe.html)

1. "Open Single Image" in Stereo Photo Maker

2. Edit > Depth Map > Create depth map from Single Image (Google AI)

3. Edit > Depth Map > Create 3D image from 2D+Depth Map...

4. File > Save .MPO File

##Sideload into your VR Headset

### The existing app by Meta is only compatible with Windows, therefore the Mac alternative is Sidequest. 

* [This video is a quick tutorial on how to get sidequest set up to a Meta Quest Headset](https://youtu.be/DApjE99iFVQ?si=fyDyNDscpcjcdZKy) 

##Enjoy!

### With Pigasus VR installed on your VR Headset, it will automatically have your VR gallery connected, and you will be able to adjust the viewing of your 3D model. 








