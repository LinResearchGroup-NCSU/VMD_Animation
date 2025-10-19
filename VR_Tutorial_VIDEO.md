# VR_Tutorial: VMD Animation to 3D Video
This is a guide to convert an animation from VMD into a 3D format that can be viewed in VR. 

## You will need:

* ImageMagick

* ffmpeg

* Owl3D

* The mobile app for your VR Headset, i.e Meta Horizon

* Sidequest

* Pigasus VR

* & VMD!

## Obtain files for high quality video

### Before creating the video,the quality of the model can be improved if all representations are set to the material *AOChalky* 

1. When the desired protein model and animation frames are loaded in, go to: Extension > Visualization > Movie Maker

2. Change the Movie Maker settings to: Renderer > Tachyon

3. Movie Settings > Trajectory with ‘4. Delete image files’ unchecked 

4. Format > Targa Frames > Make Movie!


## Generate video

1. Run this in your terminal to process .dat files:

        '/Applications/VMD 1.9.4a57-x86_64-Rev12.app/Contents/vmd/tachyon_MACOSXX86_64'  -numthreads 16 -aasamples 12 -format TGA -res 2560 1440 -o final.Video.000001.tga Video.00001.dat

### *Be sure to replace the dimensions of your model and the file names to match your own when inputting.*

### *This step may also take a long time to run; if you would like to test a video with a shorter length, utilize mdconvert to truncate your xtc file. For example:*

         mdconvert output_NVT_timecorrected.xtc --top mutl_open_CA.pdb --stride 10 -o NVT_downsampled1.xtc
        
2. Process the results from ffmpeg with this example:

        ffmpeg -threads 8 -r 10 -start_number 1 -i final.Video.%05d.tga -vcodec libx264 -crf 20 -pix_fmt yuv420p movie.mp4

###The resulting .mp4 should be at the bottom of your folder!


## Create 3D Video

1. Open Owl3D and select the movie file > 3D formats > Side-by-Side (VR/AR)

### Due to the limitations of movie length, [a potential alternative to Owl3D is this program](https://github.com/nagadomi/nunif/blob/master/iw3/README.md). However, the full extent of its settings have not been completely tested. These are the current settings that have been previously used: ![iw3 settings](https://pasteboard.co/Pp3OqabBIafU.png))


## Sideload into your VR Headset

### The existing app by Meta is only compatible with Windows, therefore the Mac alternative is Sidequest. 

* [This video is a quick tutorial on how to get sidequest set up to a Meta Quest Headset](https://youtu.be/DApjE99iFVQ?si=fyDyNDscpcjcdZKy) 

##Enjoy!

### With Pigasus VR installed on your VR Headset, it will automatically have your VR gallery connected, and you will be able to adjust the viewing of your 3D model. 
